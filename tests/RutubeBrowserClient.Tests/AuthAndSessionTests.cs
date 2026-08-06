using System.Net;

namespace RutubeBrowserClient.Tests;

public sealed class AuthAndSessionTests
{
    [Fact]
    public async Task Unauthorized_request_refreshes_token_once_and_retries_original_call()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Uri.AbsolutePath == "/api/auth/token/refresh/")
                return Task.FromResult(TestData.Json("{\"access\":\"new-access\",\"refresh\":\"new-refresh\"}"));
            var attempt = request.Headers.GetValueOrDefault("Authorization");
            return Task.FromResult(attempt == "Bearer access-secret"
                ? TestData.Json("{\"detail\":\"expired\"}", HttpStatusCode.Unauthorized)
                : TestData.Json(TestData.Fixture("identity.json")));
        });
        var store = new MemorySessionStore(TestData.Session());
        await using var client = TestData.Client(handler, store: store);

        var identity = await client.Identity.GetAsync();

        Assert.Equal("owner-42", identity.AccountId);
        Assert.Equal("new-access", store.Session!.AccessToken);
        Assert.Equal("new-refresh", store.Session.RefreshToken);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("refresh-secret", handler.Requests[1].Headers.GetValueOrDefault("Authorization") ?? "");
    }

    [Fact]
    public async Task Expiring_access_token_is_refreshed_before_the_provider_call()
    {
        var session = TestData.Session();
        session.AccessTokenExpiresAtUnix = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
        var handler = new RecordingHandler((request, _) => Task.FromResult(TestData.Json(
            request.Uri.AbsolutePath == "/api/auth/token/refresh/"
                ? "{\"access\":\"fresh-access\",\"expires_in\":3600}"
                : TestData.Fixture("identity.json"))));
        var store = new MemorySessionStore(session);
        await using var client = TestData.Client(handler, store: store);

        await client.Identity.GetAsync();

        Assert.Equal("/api/auth/token/refresh/", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("Bearer fresh-access", handler.Requests[1].Headers["Authorization"]);
        Assert.True(store.Session!.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Ensure_authenticated_uses_interactive_authenticator_then_persists_verified_identity()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(TestData.Json(TestData.Fixture("identity.json"))));
        var store = new MemorySessionStore(null);
        var authenticator = new StubAuthenticator(TestData.Session());
        await using var client = TestData.Client(handler, store: store, authenticator: authenticator);

        var identity = await client.EnsureAuthenticatedAsync();

        Assert.Equal("owner-42", identity.AccountId);
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal("Yoga Channel", store.Session!.DisplayName);
        Assert.True(store.SaveCount >= 2);
    }

    [Fact]
    public async Task Ensure_authenticated_replaces_saved_session_when_current_visitor_endpoint_returns_forbidden()
    {
        var calls = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal("/api/v2/accounts/visitor/", request.Uri.AbsolutePath);
            calls++;
            return Task.FromResult(calls == 1
                ? TestData.Json("{\"detail\":\"not authenticated\"}", HttpStatusCode.Forbidden)
                : TestData.Json(TestData.Fixture("identity.json")));
        });
        var store = new MemorySessionStore(TestData.Session("expired-access"));
        var authenticator = new StubAuthenticator(TestData.Session("replacement-access"));
        await using var client = TestData.Client(handler, store: store, authenticator: authenticator);

        var identity = await client.EnsureAuthenticatedAsync();

        Assert.Equal("owner-42", identity.AccountId);
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal("replacement-access", store.Session!.AccessToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Portable_base64_round_trip_keeps_credentials_but_string_representation_redacts_them()
    {
        var original = TestData.Session();
        var payload = RutubeSessionSerializer.ToBase64(original);
        var restored = RutubeSessionSerializer.FromBase64(payload);

        Assert.Equal("access-secret", restored.AccessToken);
        Assert.Equal("cookie-secret", restored.Cookies.Single().Value);
        Assert.DoesNotContain("access-secret", restored.ToString());
        Assert.DoesNotContain("cookie-secret", restored.Cookies.Single().ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task File_store_writes_atomic_portable_json_with_owner_only_permissions_on_unix()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rutube-client-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.json");
        try
        {
            var store = new FileRutubeSessionStore(path);
            await store.SaveAsync(TestData.Session());
            var restored = await store.LoadAsync();
            Assert.Equal("access-secret", restored!.AccessToken);
            Assert.False(File.Exists(path + ".tmp"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Provider_error_redacts_named_secrets_and_query_tokens()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(TestData.Json(
            """{"code":"invalid","message":"access_token=abc123 stream_key=rtmp-secret cookie:\"cookie-leak\" https://x.test/?token=url-secret"}""",
            HttpStatusCode.BadRequest)));
        await using var client = TestData.Client(handler);

        var exception = await Assert.ThrowsAsync<RutubeApiException>(() => client.Live.GetAsync("live-100"));

        Assert.DoesNotContain("abc123", exception.Message);
        Assert.DoesNotContain("rtmp-secret", exception.Message);
        Assert.DoesNotContain("url-secret", exception.Message);
        Assert.DoesNotContain("cookie-leak", exception.Message);
        Assert.Contains("[REDACTED]", exception.Message);
        Assert.DoesNotContain("abc123", exception.ToString());
        Assert.DoesNotContain("rtmp-secret", exception.ToString());
        Assert.DoesNotContain("url-secret", exception.ToString());
        Assert.DoesNotContain("cookie-leak", exception.ToString());
    }

    private sealed class StubAuthenticator(RutubeSession session) : IInteractiveAuthenticator
    {
        public int Calls { get; private set; }
        public Task<RutubeSession> AuthenticateAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(session); }
    }
}
