using System.Net;
using System.Text.Json;

namespace RutubeBrowserClient.Tests;

public sealed class LiveLifecycleTests
{
    [Fact]
    public async Task Create_sends_frozen_contract_fields_and_returns_redacted_ingest()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v2/video/create/stream/", request.Uri.AbsolutePath);
            return Task.FromResult(TestData.Json(TestData.Fixture("live-created.json")));
        });
        await using var client = TestData.Client(handler);
        var result = await client.Live.CreateAsync(new RutubeLiveCreateRequest
        {
            Title = "Morning practice",
            Description = "Live class",
            CategoryId = "8",
            Visibility = RutubeLiveVisibility.LinkOnly,
            PlannedStartTime = new DateTimeOffset(2026, 8, 7, 7, 0, 0, TimeSpan.Zero),
            AutoStart = true,
            StreamKeyMode = RutubeStreamKeyMode.Temporary,
            ClientReference = "event-abc"
        });

        Assert.Equal("a47c16f9db2a6e9b5fd1426596cb686d", result.Id);
        Assert.Equal(RutubeLiveStreamStatus.Waiting, result.Status);
        Assert.Equal("super-secret-key", result.Ingest!.StreamKey);
        Assert.Equal("rtmp://rtmp-lb-b.dth.rutube.ru/live_push", result.Ingest.Url!.ToString().TrimEnd('/'));
        Assert.Equal("https://rutube.ru/video/private/a47c16f9db2a6e9b5fd1426596cb686d/?p=private-playback-key", result.PlaybackUrl!.ToString());
        Assert.Equal(RutubeLiveVisibility.LinkOnly, result.Visibility);
        Assert.True(result.AutoStart);
        Assert.DoesNotContain("super-secret-key", result.ToString());
        Assert.DoesNotContain("super-secret-key", result.Ingest.ToString());
        var captured = handler.Requests.Single();
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("wait", body.RootElement.GetProperty("stream_status").GetString());
        Assert.True(body.RootElement.GetProperty("is_hidden").GetBoolean());
        Assert.True(body.RootElement.GetProperty("push_auto_start").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("stream_key_type", out _));
        Assert.False(body.RootElement.TryGetProperty("client_reference", out _));
        Assert.Equal("event-abc", captured.Headers["Idempotency-Key"]);
        Assert.Equal("Bearer access-secret", captured.Headers["Authorization"]);
        Assert.Contains("sessionid=cookie-secret", captured.Headers["Cookie"]);
        Assert.Equal("csrf-secret", captured.Headers["X-CSRFToken"]);
    }

    [Fact]
    public async Task Update_start_finish_rotate_and_delete_use_same_provider_anchor()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v2/video/stream/live-100/")
            {
                if (request.Body.Contains("deleted", StringComparison.Ordinal)) return Task.FromResult(TestData.Json("{}"));
                var fixture = TestData.Fixture("live-created.json")
                    .Replace("a47c16f9db2a6e9b5fd1426596cb686d", "live-100", StringComparison.Ordinal);
                var status = request.Body.Contains("access_status", StringComparison.Ordinal) ? "actual" :
                    request.Body.Contains("done", StringComparison.Ordinal) ? "done" : "wait";
                return Task.FromResult(TestData.Json(fixture.Replace("\"wait\"", $"\"{status}\"", StringComparison.Ordinal)));
            }
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v1/video/stream/live-100/permkey/")
                return Task.FromResult(TestData.Json("{\"perm_key\":\"rotated-secret\"}"));
            if (request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/api/v2/video/stream/live-100/")
                return Task.FromResult(TestData.Json(TestData.Fixture("live-created.json")
                    .Replace("a47c16f9db2a6e9b5fd1426596cb686d", "live-100", StringComparison.Ordinal)));
            throw new InvalidOperationException(request.Uri.ToString());
        });
        await using var client = TestData.Client(handler);

        var updated = await client.Live.UpdateAsync("live-100", new RutubeLiveUpdateRequest
        {
            Title = "Updated",
            Description = "New",
            CategoryId = "8",
            Visibility = RutubeLiveVisibility.Public,
            AutoStart = true
        });
        var started = await client.Live.StartAsync("live-100");
        var rotated = await client.Live.RotateStreamKeyAsync("live-100", RutubeStreamKeyMode.Permanent);
        var finished = await client.Live.FinishAsync("live-100");
        await client.Live.DeleteAsync("live-100");

        Assert.Equal(RutubeLiveStreamStatus.Live, started.Status);
        Assert.Equal(RutubeLiveStreamStatus.Finished, finished.Status);
        Assert.Equal("live-100", updated.Id);
        Assert.Equal("live-100", rotated.Id);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("/api/v1/video/stream/live-100/permkey/", handler.Requests[2].Uri.AbsolutePath);
        Assert.Equal("/api/v2/video/stream/live-100/", handler.Requests[3].Uri.AbsolutePath);
        using var updateBody = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(updateBody.RootElement.GetProperty("is_hidden").GetBoolean());
        Assert.True(updateBody.RootElement.GetProperty("push_auto_start").GetBoolean());
        using var startBody = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("public", startBody.RootElement.GetProperty("access_status").GetString());
        using var rotateBody = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.True(rotateBody.RootElement.GetProperty("is_active").GetBoolean());
        Assert.True(rotateBody.RootElement.GetProperty("new_key").GetBoolean());
        using var finishBody = JsonDocument.Parse(handler.Requests[4].Body);
        Assert.Equal("done", finishBody.RootElement.GetProperty("stream_status").GetString());
        using var deleteBody = JsonDocument.Parse(handler.Requests[5].Body);
        Assert.Equal("deleted", deleteBody.RootElement.GetProperty("stream_status").GetString());
    }

    [Fact]
    public async Task Use_permanent_stream_key_enables_existing_key_without_rotation_and_returns_detail()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/api/v1/video/stream/live-100/permkey/")
                return Task.FromResult(TestData.Json("{\"ok\":true}"));
            if (request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/api/v2/video/stream/live-100/")
                return Task.FromResult(TestData.Json(TestData.Fixture("live-created.json")
                    .Replace("a47c16f9db2a6e9b5fd1426596cb686d", "live-100", StringComparison.Ordinal)));
            throw new InvalidOperationException(request.Uri.ToString());
        });
        await using var client = TestData.Client(handler);

        var result = await client.Live.UsePermanentStreamKeyAsync("live-100");

        Assert.Equal("live-100", result.Id);
        Assert.Equal(2, handler.Requests.Count);
        var enable = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, enable.Method);
        Assert.Equal("/api/v1/video/stream/live-100/permkey/", enable.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(enable.Body);
        Assert.True(body.RootElement.GetProperty("is_active").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("new_key", out _));
        Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/v2/video/stream/live-100/", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Transition_hydrates_detail_when_provider_returns_ack_only()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? TestData.Json("{\"ok\":true}")
                : TestData.Json(TestData.Fixture("live-created.json"))));
        await using var client = TestData.Client(handler);

        var result = await client.Live.StartAsync("live-100");

        Assert.Equal("a47c16f9db2a6e9b5fd1426596cb686d", result.Id);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Requests.Select(x => x.Method));
    }

    [Fact]
    public async Task Reconcile_uses_current_owner_endpoint_and_immutable_fallback_without_creating()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(TestData.Json(TestData.Fixture("live-list.json"))));
        await using var client = TestData.Client(handler);

        var match = await client.Live.ReconcileOwnedAsync(new RutubeLiveReconcileRequest(
            "owner-42", "event-abc", "Morning practice", new DateTimeOffset(2026, 8, 7, 7, 0, 0, TimeSpan.Zero)));

        Assert.NotNull(match);
        Assert.Equal("a47c16f9db2a6e9b5fd1426596cb686d", match.Id);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v2/video/stream/owner/", request.Uri.AbsolutePath);
        Assert.Contains("stream_status=actual,wait,done,disable,fin_err", Uri.UnescapeDataString(request.Uri.Query));
        Assert.Contains("per_page=100", request.Uri.Query);
    }

    [Fact]
    public async Task Ambiguous_create_throws_outcome_unknown_and_never_echoes_secrets()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("stream_key=actual-secret access_token=token-secret"));
        await using var client = TestData.Client(handler);
        var request = new RutubeLiveCreateRequest
        {
            Title = "Stream",
            CategoryId = "8",
            ClientReference = "event-timeout"
        };

        var exception = await Assert.ThrowsAsync<RutubeOutcomeUnknownException>(() => client.Live.CreateAsync(request));

        Assert.Equal("event-timeout", exception.ClientReference);
        Assert.DoesNotContain("actual-secret", exception.Message);
        Assert.DoesNotContain("token-secret", exception.Message);
        Assert.DoesNotContain("actual-secret", exception.ToString());
        Assert.DoesNotContain("token-secret", exception.ToString());
        Assert.Contains("Reconcile", exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Private_contract_is_fail_closed_until_explicitly_enabled()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("network must not be called"));
        await using var client = TestData.Client(handler, enablePrivateApi: false);

        var exception = await Assert.ThrowsAsync<RutubeContractUnavailableException>(() => client.Live.GetAsync("live-100"));

        Assert.Contains("disabled", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Capability_uses_current_owner_stream_endpoint_and_paging_contract()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(TestData.Json("{\"results\":[]}")));
        await using var client = TestData.Client(handler);

        var capability = await client.Live.ProbeCapabilityAsync();

        Assert.True(capability.Available);
        Assert.Equal("studio-v2-2026-08-06-r2", capability.ContractVersion);
        var request = handler.Requests.Single();
        Assert.Equal("/api/v2/video/stream/owner/", request.Uri.AbsolutePath);
        Assert.Equal("?stream_status=wait&page=1&per_page=1", request.Uri.Query);
    }

    [Theory]
    [InlineData("WAIT", RutubeLiveStreamStatus.Waiting)]
    [InlineData("LIVE", RutubeLiveStreamStatus.Live)]
    [InlineData("actual", RutubeLiveStreamStatus.Live)]
    [InlineData("done", RutubeLiveStreamStatus.Finished)]
    [InlineData("fin_err", RutubeLiveStreamStatus.Failed)]
    [InlineData("PROCESSING", RutubeLiveStreamStatus.Processing)]
    [InlineData("PUBLISHED", RutubeLiveStreamStatus.Ready)]
    public void Provider_status_has_stable_domain_mapping(string provider, RutubeLiveStreamStatus expected) =>
        Assert.Equal(expected, RutubeModelParser.MapStatus(provider));
}
