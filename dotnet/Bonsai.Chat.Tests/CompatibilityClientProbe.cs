using System.Text.Json;
using Bonsai.Chat.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Bonsai.Chat.Tests;

// A deliberately model-free host for manual testing with real CLI clients. Only this
// explicit child-process mode starts Kestrel; normal xUnit tests use TestServer.
internal static class CompatibilityClientProbe
{
    public static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, "fixture.txt");
        await File.WriteAllTextAsync(fixture, "LOCAL_TOOL_OK\n");
        var model = new CompatibilityModel
        {
            Reply = request =>
            {
                if (request.Messages.Any(message => message.Role == "tool"))
                    return [new ModelDelta(Text: "BOSINAI_CLI_OK"), new ModelDelta(FinishReason: "stop", InputTokens: 40, OutputTokens: 6)];
                var tool = request.Tools?.FirstOrDefault(tool => tool.Name == "read")
                    ?? request.Tools?.FirstOrDefault(tool => tool.Name.EndsWith("exec_command", StringComparison.Ordinal)
                    || tool.Name.EndsWith("shell_command", StringComparison.Ordinal) || tool.Name == "bash");
                if (tool is null) return CompatibilityModel.TextReply();
                var arguments = tool.Name == "read"
                    ? JsonSerializer.Serialize(new { filePath = fixture })
                    : tool.Name == "bash"
                    ? JsonSerializer.Serialize(new { command = "echo LOCAL_TOOL_OK", description = "Local protocol smoke test" })
                    : tool.Name.EndsWith("shell_command", StringComparison.Ordinal)
                        ? JsonSerializer.Serialize(new { command = "echo LOCAL_TOOL_OK" })
                        : JsonSerializer.Serialize(new { cmd = "echo LOCAL_TOOL_OK", max_output_tokens = 100 });
                return CompatibilityModel.ToolReply(tool.Name, arguments);
            }
        };
        using var testApplication = new CompatibilityApplication(Path.Combine(directory, "history"), model);
        using var upstream = testApplication.CreateLocalClient();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var proxy = builder.Build();
        var requestNumber = 0;
        proxy.Run(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                using var input = new MemoryStream();
                await context.Request.Body.CopyToAsync(input, context.RequestAborted);
                request.Content = new ByteArrayContent(input.ToArray());
                if (context.Request.Path.StartsWithSegments("/v1"))
                    await File.WriteAllBytesAsync(Path.Combine(directory, $"request-{Interlocked.Increment(ref requestNumber):D3}.json"), input.ToArray(), context.RequestAborted);
            }
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                context.Response.Headers[header.Key] = header.Value.ToArray();
            context.Response.Headers.Remove("transfer-encoding");
            await using var output = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[4096];
            int length;
            while ((length = await output.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, length), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await proxy.StartAsync(timeout.Token);
        var address = proxy.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await File.WriteAllTextAsync(Path.Combine(directory, "base-url.txt"), address + "/v1", timeout.Token);
        Console.WriteLine("COMPAT_FAKE_HOST " + address);
        try { await proxy.WaitForShutdownAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        return 0;
    }
}
