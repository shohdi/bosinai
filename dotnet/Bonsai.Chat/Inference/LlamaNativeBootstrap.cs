using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Runtime.Intrinsics.X86;
using LLama;

namespace Bonsai.Chat.Inference;

/// <summary>
/// LLamaSharp 0.30 ships net10 and netstandard binaries. On .NET 8 its netstandard
/// binary omits the library resolver, so load the packaged native dependencies explicitly.
/// </summary>
public static class LlamaNativeBootstrap
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IntPtr> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            var assembly = typeof(LLamaWeights).Assembly;
            var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            if (framework?.StartsWith(".NETStandard", StringComparison.Ordinal) != true)
            {
                // Newer framework-specific LLamaSharp binaries own their resolver.
                _loaded = true;
                return;
            }

            var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
            var prefix = OperatingSystem.IsWindows() ? "" : "lib";
            var directories = new List<string>();
            var bases = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(assembly.Location)! }.Distinct();
            foreach (var root in bases)
            {
                var native = Path.Combine(root, "runtimes", platform + "-" + architecture, "native");
                if (architecture == "x64" && !OperatingSystem.IsMacOS())
                {
                    if (Avx2.IsSupported) directories.Add(Path.Combine(native, "avx2"));
                    if (Avx.IsSupported) directories.Add(Path.Combine(native, "avx"));
                    directories.Add(Path.Combine(native, "noavx"));
                }
                directories.Add(native);
            }

            var directory = directories.FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, prefix + "llama" + extension)))
                ?? throw new DllNotFoundException("The LLamaSharp native CPU backend was not found in the application runtimes folder.");
            var loaded = new List<IntPtr>();
            try
            {
                // Windows does not automatically search nested runtime folders for DLL dependencies.
                foreach (var name in new[] { "ggml-base", "ggml-cpu", "ggml-metal", "ggml-blas", "ggml", "llama" })
                {
                    var path = Path.Combine(directory, prefix + name + extension);
                    if (!File.Exists(path))
                    {
                        if (name is "ggml-metal" or "ggml-blas") continue;
                        throw new DllNotFoundException("Missing LLamaSharp native dependency: " + path);
                    }
                    var handle = NativeLibrary.Load(path);
                    Libraries.Add(name, handle);
                    loaded.Add(handle);
                }
                NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
                {
                    var key = Path.GetFileNameWithoutExtension(name);
                    if (key.StartsWith("lib", StringComparison.Ordinal)) key = key[3..];
                    return Libraries.TryGetValue(key, out var handle) ? handle : IntPtr.Zero;
                });
                // Handles remain loaded for the lifetime of LLamaSharp's static native backend.
                _loaded = true;
            }
            catch
            {
                Libraries.Clear();
                foreach (var handle in loaded.AsEnumerable().Reverse()) NativeLibrary.Free(handle);
                throw;
            }
        }
    }
}
