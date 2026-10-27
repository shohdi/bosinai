using System.Diagnostics;
using System.Runtime.InteropServices;
using Bonsai.Chat.Inference;
using LLama.Native;
using Xunit;

namespace Bonsai.Chat.Tests;

public sealed class NativeBackendTests
{
    [WindowsX64Fact]
    public async Task PackagedCpuBackendLoadsInAFreshProcessWithoutModelWeights()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(typeof(NativeBackendTests).Assembly.Location);
        start.ArgumentList.Add("--native-backend-smoke");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var stdout = await output;
        var stderr = await errors;
        Assert.True(process.ExitCode == 0, $"Native probe failed ({process.ExitCode}).\n{stdout}\n{stderr}");
        Assert.Contains("NATIVE_CPU_OK", stdout);
    }

    private sealed class WindowsX64FactAttribute : FactAttribute
    {
        public WindowsX64FactAttribute()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                Skip = "This regression exercises the packaged Windows x64 CPU native loader.";
        }
    }
}

// Microsoft.NET.Test.Sdk normally emits an empty entry point. This explicit probe runs
// only as a fresh child process, so native DLLs from other tests cannot hide a loader bug.
internal static class NativeBackendProbe
{
    public static int Main(string[] arguments)
    {
        if (!arguments.SequenceEqual(new[] { "--native-backend-smoke" })) return 64;
        try
        {
            LlamaNativeBootstrap.EnsureLoaded();
            NativeApi.llama_empty_call();
            var maxDevices = NativeApi.llama_max_devices();
            var nativeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native"))
                + Path.DirectorySeparatorChar;
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>()
                .Where(module => module.ModuleName.Equals("llama.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.StartsWith("ggml", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!modules.Any(module => module.ModuleName.Equals("llama.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The llama CPU library was not loaded.");
            foreach (var module in modules)
                if (!Path.GetFullPath(module.FileName).StartsWith(nativeRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Loaded a native model library outside the packaged CPU backend: " + module.FileName);
            Console.WriteLine($"NATIVE_CPU_OK max_devices={maxDevices}; libraries={string.Join(',', modules.Select(module => module.ModuleName))}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
