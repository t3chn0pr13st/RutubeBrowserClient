using System.Net;
using System.Reflection;
using System.Text;

namespace RutubeBrowserClient.Tests;

internal sealed class MemorySessionStore(RutubeSession? session) : IRutubeSessionStore
{
    public RutubeSession? Session { get; private set; } = session;
    public int SaveCount { get; private set; }
    public Task<RutubeSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);
    public Task SaveAsync(RutubeSession session, CancellationToken cancellationToken = default)
    {
        Session = session;
        SaveCount++;
        return Task.CompletedTask;
    }
    public Task ClearAsync(CancellationToken cancellationToken = default) { Session = null; return Task.CompletedTask; }
}

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string Body,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType);

internal sealed class RecordingHandler(
    Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var contentHeaders = request.Content is null ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>() : request.Content.Headers;
        var headers = request.Headers.Concat(contentHeaders)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => string.Join(",", x.SelectMany(v => v.Value)), StringComparer.OrdinalIgnoreCase);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, body, headers, request.Content?.Headers.ContentType?.ToString());
        Requests.Add(captured);
        return await responder(captured, cancellationToken);
    }
}

internal static class TestData
{
    public static RutubeSession Session(string accessToken = "access-secret", string? refreshToken = "refresh-secret") => new()
    {
        AccountId = "owner-42",
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        CsrfToken = "csrf-secret",
        UserAgent = "RutubeBrowserClient.Tests",
        Cookies = [new RutubeCookie { Name = "sessionid", Value = "cookie-secret", Domain = ".rutube.ru", Secure = true }]
    };

    public static string Fixture(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    public static RutubeClient Client(
        RecordingHandler api,
        RecordingHandler? upload = null,
        MemorySessionStore? store = null,
        bool enablePrivateApi = true,
        IInteractiveAuthenticator? authenticator = null)
    {
        store ??= new MemorySessionStore(Session());
        var options = new RutubeClientOptions
        {
            EnablePrivateStudioApi = enablePrivateApi,
            StudioApiBaseUrl = "https://studio.test/api/",
            PublicApiBaseUrl = "https://public.test/api/",
            UploadBaseUrl = "https://upload.test/",
            StudioBaseUrl = "https://studio.test/",
            ApiHttpMessageHandlerFactory = () => api,
            UploadHttpMessageHandlerFactory = () => upload ?? api,
            AuthenticatorFactory = authenticator is null ? null : _ => authenticator
        };
        return new RutubeClient(store, options);
    }
}
