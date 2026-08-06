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
            StreamKeyMode = RutubeStreamKeyMode.Temporary,
            ClientReference = "event-abc"
        });

        Assert.Equal("live-100", result.Id);
        Assert.Equal(RutubeLiveStreamStatus.Waiting, result.Status);
        Assert.Equal("super-secret-key", result.Ingest!.StreamKey);
        Assert.DoesNotContain("super-secret-key", result.ToString());
        Assert.DoesNotContain("super-secret-key", result.Ingest.ToString());
        var captured = handler.Requests.Single();
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("WAIT", body.RootElement.GetProperty("stream_status").GetString());
        Assert.True(body.RootElement.GetProperty("is_hidden").GetBoolean());
        Assert.Equal("temporary", body.RootElement.GetProperty("stream_key_type").GetString());
        Assert.Equal("event-abc", body.RootElement.GetProperty("client_reference").GetString());
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
                if (request.Body.Contains("DELETE", StringComparison.Ordinal)) return Task.FromResult(TestData.Json("{}"));
                var fixture = TestData.Fixture("live-created.json");
                var status = request.Body.Contains("START", StringComparison.Ordinal) ? "LIVE" :
                    request.Body.Contains("END", StringComparison.Ordinal) ? "FINISHED" : "WAIT";
                return Task.FromResult(TestData.Json(fixture.Replace("\"WAIT\"", $"\"{status}\"", StringComparison.Ordinal)));
            }
            throw new InvalidOperationException(request.Uri.ToString());
        });
        await using var client = TestData.Client(handler);

        var updated = await client.Live.UpdateAsync("live-100", new RutubeLiveUpdateRequest
        {
            Title = "Updated",
            Description = "New",
            CategoryId = "8",
            Visibility = RutubeLiveVisibility.Public
        });
        var started = await client.Live.StartAsync("live-100");
        var rotated = await client.Live.RotateStreamKeyAsync("live-100", RutubeStreamKeyMode.Permanent);
        var finished = await client.Live.FinishAsync("live-100");
        await client.Live.DeleteAsync("live-100");

        Assert.Equal(RutubeLiveStreamStatus.Live, started.Status);
        Assert.Equal(RutubeLiveStreamStatus.Finished, finished.Status);
        Assert.Equal("live-100", updated.Id);
        Assert.Equal("live-100", rotated.Id);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, x => Assert.Equal("/api/v2/video/stream/live-100/", x.Uri.AbsolutePath));
        using var updateBody = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(updateBody.RootElement.GetProperty("is_hidden").GetBoolean());
        using var rotateBody = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.True(rotateBody.RootElement.GetProperty("regenerate_stream_key").GetBoolean());
        Assert.Equal("permanent", rotateBody.RootElement.GetProperty("stream_key_type").GetString());
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

        Assert.Equal("live-100", result.Id);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Requests.Select(x => x.Method));
    }

    [Fact]
    public async Task Reconcile_filters_owned_stream_by_client_reference_without_creating()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(TestData.Json(TestData.Fixture("live-list.json"))));
        await using var client = TestData.Client(handler);

        var match = await client.Live.ReconcileOwnedAsync(new RutubeLiveReconcileRequest("owner-42", "event-abc"));

        Assert.NotNull(match);
        Assert.Equal("live-100", match.Id);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("owner_id=owner-42", request.Uri.Query);
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

    [Theory]
    [InlineData("WAIT", RutubeLiveStreamStatus.Waiting)]
    [InlineData("LIVE", RutubeLiveStreamStatus.Live)]
    [InlineData("PROCESSING", RutubeLiveStreamStatus.Processing)]
    [InlineData("PUBLISHED", RutubeLiveStreamStatus.Ready)]
    public void Provider_status_has_stable_domain_mapping(string provider, RutubeLiveStreamStatus expected) =>
        Assert.Equal(expected, RutubeModelParser.MapStatus(provider));
}
