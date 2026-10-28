using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bonsai.Chat.Inference;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Bonsai.Chat.Tests;

public sealed class ChatApiTests
{
    private const string Reply = "Here is Python:\n\n```python\nprint('violet')\n```\n\n**Done.**";

    [Fact]
    public async Task SessionLifecyclePersistsAcrossServerRestart()
    {
        using var directory = new HistoryDirectory();
        string id;
        using (var app = new ChatApplication(directory.Path))
        using (var client = app.CreateLocalClient())
        {
            Assert.Empty((await GetJson(client, "/api/sessions")).GetProperty("sessions").EnumerateArray());
            var session = await CreateSession(client, "  Project notes  ");
            id = session.GetProperty("id").GetString()!;
            Assert.Equal("Project notes", session.GetProperty("title").GetString());
            Assert.Equal(0, session.GetProperty("message_count").GetInt32());
            Assert.Empty(session.GetProperty("messages").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, session.GetProperty("active_job").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("created_at").GetString()));
            using var renamed = await client.PatchAsJsonAsync($"/api/sessions/{id}", new { title = "Renamed chat" });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }

        using (var app = new ChatApplication(directory.Path))
        using (var client = app.CreateLocalClient())
        {
            var session = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session");
            Assert.Equal("Renamed chat", session.GetProperty("title").GetString());
            Assert.Equal(id, (await GetJson(client, "/api/sessions")).GetProperty("sessions")[0].GetProperty("id").GetString());
            using var deleted = await client.DeleteAsync($"/api/sessions/{id}");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sessions/{id}")).StatusCode);
            Assert.Empty((await GetJson(client, "/api/sessions")).GetProperty("sessions").EnumerateArray());
            Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, id + ".json")));
        }
    }

    [Fact]
    public async Task CompletedTurnsPreserveMarkdownAndPassFullHistoryToModel()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel();
        string id;
        using (var app = new ChatApplication(directory.Path, model))
        using (var client = app.CreateLocalClient())
        {
            id = (await CreateSession(client)).GetProperty("id").GetString()!;
            var otherId = (await CreateSession(client)).GetProperty("id").GetString()!;
            var first = await FinishJob(client, await Send(client, id, "Remember violet."));
            Assert.Equal("completed", first.GetProperty("status").GetString());
            Assert.Equal(Reply, first.GetProperty("session").GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Equal(JsonValueKind.Null, first.GetProperty("error").ValueKind);
            Assert.Equal("Remember violet.", first.GetProperty("session").GetProperty("title").GetString());

            Assert.Equal("completed", (await FinishJob(client, await Send(client, id, "Repeat my word."))).GetProperty("status").GetString());
            Assert.Equal(2, model.Histories.Count);
            Assert.Contains(model.Histories[1], message => message.Role == "user" && message.Content == "Remember violet.");
            Assert.Contains(model.Histories[1], message => message.Role == "assistant" && message.Content == Reply);
            Assert.Contains(model.Histories[1], message => message.Role == "user" && message.Content == "Repeat my word.");
            Assert.Equal(4, (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session").GetProperty("message_count").GetInt32());
            Assert.Empty((await GetJson(client, $"/api/sessions/{otherId}")).GetProperty("session").GetProperty("messages").EnumerateArray());
        }

        using var restarted = new ChatApplication(directory.Path);
        using var restartedClient = restarted.CreateLocalClient();
        var resumed = (await GetJson(restartedClient, $"/api/sessions/{id}")).GetProperty("session");
        Assert.Equal(4, resumed.GetProperty("message_count").GetInt32());
        Assert.Equal(Reply, resumed.GetProperty("messages")[3].GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, resumed.GetProperty("active_job").ValueKind);
    }

    [Fact]
    public async Task LongMessageUsesPreparedSuffixInGenerationAndPersistedHistory()
    {
        using var directory = new HistoryDirectory();
        const string suffix = "Keep the latest instructions 🧠";
        var input = "Discard this old prefix. " + new string('x', 300_000) + "\n" + suffix;
        var model = new FakeChatModel();
        string? preparedInput = null;
        model.Prepare = (content, _) =>
        {
            preparedInput = content;
            return Task.FromResult(content[^suffix.Length..]);
        };
        string id;
        using (var app = new ChatApplication(directory.Path, model))
        using (var client = app.CreateLocalClient())
        {
            id = (await CreateSession(client, "Long input")).GetProperty("id").GetString()!;
            var completed = await FinishJob(client, await Send(client, id, input));
            Assert.Equal("completed", completed.GetProperty("status").GetString());
            Assert.Equal(input, preparedInput);
            Assert.Equal(suffix, completed.GetProperty("content").GetString());
            Assert.Equal(suffix, Assert.Single(model.Histories).Single().Content);
            Assert.Equal(suffix, completed.GetProperty("session").GetProperty("messages")[0].GetProperty("content").GetString());
        }

        using var restarted = new ChatApplication(directory.Path);
        using var restartedClient = restarted.CreateLocalClient();
        var saved = (await GetJson(restartedClient, $"/api/sessions/{id}")).GetProperty("session");
        Assert.Equal(suffix, saved.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(Reply, saved.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task InputPreparationChangesOnlyTheNewMessageAndPreservesEarlierHistory()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel();
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        await FinishJob(client, await Send(client, id, "Keep my earlier request."));
        model.Prepare = (content, _) => Task.FromResult(content[content.IndexOf("latest", StringComparison.Ordinal)..]);

        var completed = await FinishJob(client, await Send(client, id, "Discard this beginning; latest instruction."));

        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(new[]
        {
            new InferenceMessage("user", "Keep my earlier request."),
            new InferenceMessage("assistant", Reply),
            new InferenceMessage("user", "latest instruction.")
        }, model.Histories[1]);
        var messages = completed.GetProperty("session").GetProperty("messages");
        Assert.Equal("Keep my earlier request.", messages[0].GetProperty("content").GetString());
        Assert.Equal("latest instruction.", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CancellingInputPreparationPreservesHistoryAndReleasesTheConversation()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel();
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        await FinishJob(client, await Send(client, id, "Saved request"));
        var before = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session").GetProperty("messages").GetRawText();
        var preparationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Prepare = async (content, cancellationToken) =>
        {
            preparationStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return content;
        };
        var jobId = await Send(client, id, "Cancel before inference starts");
        await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancelled = await client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(cancelled)).GetProperty("job").GetProperty("status").GetString());
        Assert.Single(model.Histories);
        var saved = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session");
        Assert.Equal(before, saved.GetProperty("messages").GetRawText());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("active_job").ValueKind);

        model.Prepare = null;
        Assert.Equal("completed", (await FinishJob(client, await Send(client, id, "Retry"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task StopRejectsBusyMutationsAndNeverCommitsPartialTurn()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel { Mode = ReplyMode.Block };
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        var jobId = await Send(client, id, "Wait for cancellation");
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var detail = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session");
        Assert.Equal(jobId, detail.GetProperty("active_job").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { content = "Duplicate" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/sessions/{id}", new { title = "Busy" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/sessions/{id}")).StatusCode);

        using var cancelled = await client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await ReadJson(cancelled)).GetProperty("job").GetProperty("status").GetString());
        using var again = await client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(again)).GetProperty("job").GetProperty("status").GetString());
        detail = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session");
        Assert.Empty(detail.GetProperty("messages").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("active_job").ValueKind);

        model.Mode = ReplyMode.Complete;
        Assert.Equal("completed", (await FinishJob(client, await Send(client, id, "Try again"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task FailedGenerationLeavesCompletedHistoryIntactAndCanRetry()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel();
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        await FinishJob(client, await Send(client, id, "Saved request"));
        var before = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session").GetProperty("messages").GetRawText();
        model.Mode = ReplyMode.Fail;
        var failed = await FinishJob(client, await Send(client, id, "This request fails"));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("error").GetString()));
        var detail = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session");
        Assert.Equal(before, detail.GetProperty("messages").GetRawText());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("active_job").ValueKind);
        model.Mode = ReplyMode.Complete;
        Assert.Equal("completed", (await FinishJob(client, await Send(client, id, "Retry"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancellingQueuedConversationDoesNotInterruptRunningConversation()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel { Mode = ReplyMode.Block };
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var firstId = (await CreateSession(client)).GetProperty("id").GetString()!;
        var secondId = (await CreateSession(client)).GetProperty("id").GetString()!;
        var firstJob = await Send(client, firstId, "First");
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondJob = await Send(client, secondId, "Queued");
        using var stopped = await client.PostAsync($"/api/jobs/{secondJob}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(stopped)).GetProperty("job").GetProperty("status").GetString());
        Assert.Equal("running", (await GetJson(client, $"/api/jobs/{firstJob}")).GetProperty("job").GetProperty("status").GetString());
        Assert.Empty((await GetJson(client, $"/api/sessions/{secondId}")).GetProperty("session").GetProperty("messages").EnumerateArray());
        using var stopFirst = await client.PostAsync($"/api/jobs/{firstJob}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(stopFirst)).GetProperty("job").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ConcurrentSubmissionsAdmitOnlyOneTurnForTheSameConversation()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel { Mode = ReplyMode.Block };
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { content = "Request " + index })));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(11, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            var accepted = responses.Single(response => response.StatusCode == HttpStatusCode.Accepted);
            var jobId = (await ReadJson(accepted)).GetProperty("job").GetProperty("id").GetString();
            using var cancelled = await client.PostAsync($"/api/jobs/{jobId}/cancel", null);
            Assert.Equal("cancelled", (await ReadJson(cancelled)).GetProperty("job").GetProperty("status").GetString());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task GlobalQueueHasBoundedAdmissionAndReleasesCapacityAfterCancellation()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel { Mode = ReplyMode.Block };
        using var app = new ChatApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        var jobs = new List<string>();
        var firstSession = (await CreateSession(client)).GetProperty("id").GetString()!;
        jobs.Add(await Send(client, firstSession, "Running request"));
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 7; index++)
        {
            var sessionId = (await CreateSession(client)).GetProperty("id").GetString()!;
            jobs.Add(await Send(client, sessionId, "Queued " + index));
        }
        var overflowSession = (await CreateSession(client)).GetProperty("id").GetString()!;
        using var overflow = await client.PostAsJsonAsync($"/api/sessions/{overflowSession}/messages", new { content = "Overflow" });
        Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
        using var stopped = await client.PostAsync($"/api/jobs/{jobs[^1]}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(stopped)).GetProperty("job").GetProperty("status").GetString());
        jobs.Add(await Send(client, overflowSession, "Capacity released"));
        foreach (var jobId in jobs)
        {
            using var cancelled = await client.PostAsync($"/api/jobs/{jobId}/cancel", null);
            Assert.Equal("cancelled", (await ReadJson(cancelled)).GetProperty("job").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task SeparateAppInstancesCannotWriteTheSameConversationDuringGeneration()
    {
        using var directory = new HistoryDirectory();
        var model = new FakeChatModel { Mode = ReplyMode.Block };
        using var firstApp = new ChatApplication(directory.Path, model);
        using var firstClient = firstApp.CreateLocalClient();
        var id = (await CreateSession(firstClient)).GetProperty("id").GetString()!;
        var jobId = await Send(firstClient, id, "Own the file lease");
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var secondApp = new ChatApplication(directory.Path);
        using var secondClient = secondApp.CreateLocalClient();
        Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await secondClient.PostAsJsonAsync($"/api/sessions/{id}/messages", new { content = "Other instance" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await secondClient.PatchAsJsonAsync($"/api/sessions/{id}", new { title = "Other instance" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await secondClient.DeleteAsync($"/api/sessions/{id}")).StatusCode);

        using var cancelled = await firstClient.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(cancelled)).GetProperty("job").GetProperty("status").GetString());
        using var renamed = await secondClient.PatchAsJsonAsync($"/api/sessions/{id}", new { title = "Lease released" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    [Theory]
    [InlineData("{\"content\":\"\"}")]
    [InlineData("{\"content\":\"  \\n\\t\"}")]
    [InlineData("{\"content\":null}")]
    [InlineData("{\"content\":13}")]
    [InlineData("{\"content\":[]}")]
    public async Task InvalidMessageCannotChangeHistory(string json)
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        using var response = await client.PostAsync($"/api/sessions/{id}/messages", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ReadJson(response)).TryGetProperty("error", out _));
        Assert.Empty((await GetJson(client, $"/api/sessions/{id}")).GetProperty("session").GetProperty("messages").EnumerateArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task InvalidTitleRejectedOnCreateAndRename(string? title)
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        using var invalidCreate = await client.PostAsJsonAsync("/api/sessions", new { title });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        var id = (await CreateSession(client, "Original")).GetProperty("id").GetString()!;
        using var invalidRename = await client.PatchAsJsonAsync($"/api/sessions/{id}", new { title });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRename.StatusCode);
        Assert.Equal("Original", (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session").GetProperty("title").GetString());
    }

    [Fact]
    public async Task MalformedOversizedAndMissingRequestsReturnApiErrors()
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/sessions", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/sessions", new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/sessions", Array.Empty<string>())).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync("/api/sessions", new { title = new string('x', 256_001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/sessions", new { title = new string('x', 121) })).StatusCode);
        var id = (await CreateSession(client)).GetProperty("id").GetString()!;
        Assert.Equal("completed", (await FinishJob(client, await Send(client, id, new string('x', 100_001)))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sessions/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/jobs/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/jobs/missing/cancel", null)).StatusCode);
    }

    [Theory]
    [InlineData("..%5Cescape")]
    [InlineData("..escape")]
    [InlineData("bad.name")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task InvalidSessionIdsAreRejected(string id)
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task CorruptHistoryIsSkippedWithoutBreakingUsableSessions()
    {
        using var directory = new HistoryDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "broken.json"), "not JSON");
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "unsupported.json"), "{\"version\":2,\"messages\":[]}");
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        var id = (await CreateSession(client, "Readable")).GetProperty("id").GetString();
        var sessions = (await GetJson(client, "/api/sessions")).GetProperty("sessions");
        Assert.Single(sessions.EnumerateArray());
        Assert.Equal(id, sessions[0].GetProperty("id").GetString());
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/sessions/broken")).StatusCode);
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/sessions/unsupported")).StatusCode);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task InvalidVersionValueCannotBreakHistoryListing(string version)
    {
        using var directory = new HistoryDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "bad_version.json"),
            "{\"version\":" + version + ",\"messages\":[]}");
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        var session = await CreateSession(client, "Readable");
        Assert.Single((await GetJson(client, "/api/sessions")).GetProperty("sessions").EnumerateArray());
        Assert.Equal((HttpStatusCode)422, (await client.GetAsync("/api/sessions/bad_version")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions/" + session.GetProperty("id").GetString())).StatusCode);
    }

    [Fact]
    public async Task MarkdownAssetsWorkOfflineAndApiResponsesAreNotCached()
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<!doctype html>", (await page.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Contains("frame-ancestors 'none'", string.Join(" ", page.Headers.GetValues("Content-Security-Policy")));
        foreach (var asset in new[] { "marked.min.js", "purify.min.js", "highlight.min.js", "highlight.css" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/static/vendor/{asset}")).StatusCode);
        using var sessions = await client.GetAsync("/api/sessions");
        Assert.True(sessions.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/static/..%5C..%5CProgram.cs")).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost:9000")]
    [InlineData("https://localhost")]
    [InlineData("http://user@localhost")]
    public async Task ForeignOriginsCannotMutateHistory(string origin)
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(new { }) };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var blocked = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Empty((await GetJson(client, "/api/sessions")).GetProperty("sessions").EnumerateArray());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task LoopbackHostsAcceptTheirOwnOrigin(string host)
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(new { }) };
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("Origin", "http://" + host);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UntrustedHostAndCrossSiteFetchAreRejected()
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/api/sessions");
        foreignHost.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(foreignHost)).StatusCode);
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(new { }) };
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
    }

    [Fact]
    public async Task ModelCatalogListsBothChoicesAndRejectsUnknownModel()
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        var result = await GetJson(client, "/api/models");
        var ids = result.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("id").GetString()).ToArray();
        Assert.Contains("deepseek", ids);
        Assert.Contains("bonsai", ids);
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("selected_id").GetString()));
        using var invalid = await client.PostAsJsonAsync("/api/models/select", new { id = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await ReadJson(invalid)).TryGetProperty("error", out _));
        Assert.Equal(result.GetProperty("selected_id").GetString(),
            (await GetJson(client, "/api/models")).GetProperty("selected_id").GetString());
    }

    [Fact]
    public async Task ModelSelectionIsRejectedWhileAnyConversationIsGenerating()
    {
        using var directory = new HistoryDirectory();
        var modelPath = System.IO.Path.Combine(directory.Path, "test-model.gguf");
        await File.WriteAllTextAsync(modelPath, "Model loading is deliberately not invoked by this test.");
        var model = new FakeChatModel { Mode = ReplyMode.Block };
        using var app = new ChatApplication(directory.Path, model, new Dictionary<string, string?>
        {
            ["Llama:ModelPath"] = modelPath
        });
        using var client = app.CreateLocalClient();
        var sessionId = (await CreateSession(client)).GetProperty("id").GetString()!;
        var jobId = await Send(client, sessionId, "Block switching");
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var blocked = await client.PostAsJsonAsync("/api/models/select", new { id = "deepseek" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var cancelled = await client.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal("cancelled", (await ReadJson(cancelled)).GetProperty("job").GetProperty("status").GetString());
        using var selected = await client.PostAsJsonAsync("/api/models/select", new { id = "deepseek" });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal("deepseek", (await ReadJson(selected)).GetProperty("selected_id").GetString());
    }

    [Fact]
    public async Task AvailableModelSelectionPersistsAcrossRestartWithoutLoadingWeights()
    {
        using var directory = new HistoryDirectory();
        var modelPath = System.IO.Path.Combine(directory.Path, "bonsai.gguf");
        var runtimePath = System.IO.Path.Combine(directory.Path, "llama-server.exe");
        await File.WriteAllTextAsync(modelPath, "Weights are loaded only on generation.");
        await File.WriteAllTextAsync(runtimePath, "Runtime is started only on generation.");
        var settings = new Dictionary<string, string?>
        {
            ["Models:BonsaiModelPath"] = modelPath,
            ["Models:BonsaiRuntimePath"] = runtimePath
        };
        using (var app = new ChatApplication(directory.Path, settings: settings))
        using (var client = app.CreateLocalClient())
        {
            var models = (await GetJson(client, "/api/models")).GetProperty("models");
            var bonsai = models.EnumerateArray().Single(model => model.GetProperty("id").GetString() == "bonsai");
            Assert.True(bonsai.GetProperty("available").GetBoolean());
            using var selection = await client.PostAsJsonAsync("/api/models/select", new { id = "bonsai" });
            Assert.Equal(HttpStatusCode.OK, selection.StatusCode);
            Assert.Equal("bonsai", (await ReadJson(selection)).GetProperty("selected_id").GetString());
            Assert.Equal("bonsai", (await GetJson(client, "/api/status")).GetProperty("selected_model_id").GetString());
        }

        using var restarted = new ChatApplication(directory.Path, settings: settings);
        using var restartedClient = restarted.CreateLocalClient();
        Assert.Equal("bonsai", (await GetJson(restartedClient, "/api/models")).GetProperty("selected_id").GetString());
        Assert.Equal("bonsai", (await GetJson(restartedClient, "/api/status")).GetProperty("selected_model_id").GetString());
        var selectionPath = System.IO.Path.Combine(directory.Path, "metadata", "selected-model.json");
        Assert.True(File.Exists(selectionPath));
        Assert.Contains("bonsai", await File.ReadAllTextAsync(selectionPath));
    }

    [Theory]
    [InlineData("{\"selected_id\":13}")]
    [InlineData("{\"selected_id\":{}}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task MalformedSavedModelSelectionFallsBackToUsableCatalog(string saved)
    {
        using var directory = new HistoryDirectory();
        var metadataDirectory = System.IO.Path.Combine(directory.Path, "metadata");
        Directory.CreateDirectory(metadataDirectory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(metadataDirectory, "selected-model.json"), saved);
        using var app = new ChatApplication(directory.Path);
        using var client = app.CreateLocalClient();
        var result = await GetJson(client, "/api/models");
        Assert.Contains(result.GetProperty("selected_id").GetString(), new[] { "deepseek", "bonsai" });
        Assert.Equal(2, result.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task MissingModelFailsActionablyWithoutSavingAPartialTurn()
    {
        using var directory = new HistoryDirectory();
        using var app = new ChatApplication(directory.Path, useRealModel: true);
        using var client = app.CreateLocalClient();
        var status = (await GetJson(client, "/api/status")).GetProperty("model");
        Assert.False(status.GetProperty("online").GetBoolean());
        Assert.Equal("missing", status.GetProperty("state").GetString());
        var sessionId = (await CreateSession(client)).GetProperty("id").GetString()!;
        var failed = await FinishJob(client, await Send(client, sessionId, "Try missing weights"));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("GGUF", failed.GetProperty("error").GetString());
        Assert.Empty((await GetJson(client, $"/api/sessions/{sessionId}")).GetProperty("session").GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task InvalidNativeRuntimeFailureCleansUpAndLeavesModelSwitchingUsable()
    {
        using var directory = new HistoryDirectory();
        var modelPath = System.IO.Path.Combine(directory.Path, "test.gguf");
        var runtimePath = System.IO.Path.Combine(directory.Path, "invalid-native-runtime.exe");
        await File.WriteAllTextAsync(modelPath, "This test never loads GGUF weights.");
        await File.WriteAllTextAsync(runtimePath, "This file is deliberately not an executable.");
        using var app = new ChatApplication(directory.Path, settings: new Dictionary<string, string?>
        {
            ["Llama:ModelPath"] = modelPath,
            ["Models:BonsaiModelPath"] = modelPath,
            ["Models:BonsaiRuntimePath"] = runtimePath
        }, useRealModel: true);
        using var client = app.CreateLocalClient();
        using var selected = await client.PostAsJsonAsync("/api/models/select", new { id = "bonsai" });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var sessionId = (await CreateSession(client)).GetProperty("id").GetString()!;
        var failed = await FinishJob(client, await Send(client, sessionId, "Test startup failure"));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Contains("Could not start Bonsai", failed.GetProperty("error").GetString());
        Assert.Equal("error", (await GetJson(client, "/api/status")).GetProperty("model").GetProperty("state").GetString());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.key", SearchOption.AllDirectories));
        Assert.Empty((await GetJson(client, $"/api/sessions/{sessionId}")).GetProperty("session").GetProperty("messages").EnumerateArray());
        using var switched = await client.PostAsJsonAsync("/api/models/select", new { id = "deepseek" });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
    }

    private static async Task<JsonElement> CreateSession(HttpClient client, string? title = null)
    {
        using var response = title is null
            ? await client.PostAsJsonAsync("/api/sessions", new { })
            : await client.PostAsJsonAsync("/api/sessions", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response)).GetProperty("session");
    }

    private static async Task<string> Send(HttpClient client, string id, string content)
    {
        using var response = await client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { content });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await ReadJson(response)).GetProperty("job").GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> FinishJob(HttpClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var job = (await GetJson(client, $"/api/jobs/{id}")).GetProperty("job");
            if (job.GetProperty("status").GetString() is not ("pending" or "running")) return job;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private enum ReplyMode { Complete, Block, Fail }

    private sealed class FakeChatModel : IChatModel
    {
        public ReplyMode Mode { get; set; }
        public Func<string, CancellationToken, Task<string>>? Prepare { get; set; }
        public List<InferenceMessage[]> Histories { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelStatus GetStatus() => new("ready", "Test GGUF", "Ready", true);

        public Task<string> PrepareInputAsync(string content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Prepare?.Invoke(content, cancellationToken) ?? Task.FromResult(content);
        }

        public async IAsyncEnumerable<string> GenerateAsync(IReadOnlyList<InferenceMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Histories.Add(messages.ToArray());
            Started.TrySetResult();
            if (Mode == ReplyMode.Block)
            {
                yield return "Partial reply that must not be saved";
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Mode == ReplyMode.Fail)
            {
                yield return "Partial failure";
                throw new InvalidOperationException("Test model failure");
            }
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return Reply[..20];
            yield return Reply[20..];
        }
    }

    private sealed class ChatApplication(string directory, FakeChatModel? model = null,
        IReadOnlyDictionary<string, string?>? settings = null, bool useRealModel = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var values = new Dictionary<string, string?>
            {
                ["Chat:DataDirectory"] = directory,
                ["Models:SelectionFile"] = System.IO.Path.Combine(directory, "metadata", "selected-model.json"),
                ["Llama:ModelPath"] = System.IO.Path.Combine(directory, "missing-deepseek.gguf"),
                ["Models:BonsaiModelPath"] = System.IO.Path.Combine(directory, "missing-bonsai.gguf"),
                ["Models:BonsaiRuntimePath"] = System.IO.Path.Combine(directory, "missing-runtime")
            };
            if (settings is not null)
                foreach (var (key, value) in settings) values[key] = value;
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));
            builder.ConfigureServices(services =>
            {
                if (!useRealModel)
                {
                    services.RemoveAll<IChatModel>();
                    services.AddSingleton<IChatModel>(model ?? new FakeChatModel());
                }
            });
        }

        public HttpClient CreateLocalClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });
    }

    private sealed class HistoryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bonsai-dotnet-tests", Guid.NewGuid().ToString("N"));
        public HistoryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var expectedParent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bonsai-dotnet-tests"));
            if (!string.Equals(System.IO.Path.GetDirectoryName(fullPath), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its temporary directory.");
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
