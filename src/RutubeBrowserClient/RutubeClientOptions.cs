namespace RutubeBrowserClient;

/// <summary>Runtime and transport settings for <see cref="RutubeClient"/>.</summary>
public sealed class RutubeClientOptions
{
    public string WebBaseUrl { get; set; } = "https://rutube.ru";
    public string StudioBaseUrl { get; set; } = "https://studio.rutube.ru";
    public string StudioApiBaseUrl { get; set; } = "https://studio.rutube.ru/api/";
    public string PublicApiBaseUrl { get; set; } = "https://rutube.ru/api/";
    public string LoginUrl { get; set; } = "https://studio.rutube.ru/";
    public string IdentityPath { get; set; } = "v2/accounts/visitor/?client=vulp";
    public string CategoriesPath { get; set; } = "v2/video/categories/";
    public string VideoPath { get; set; } = "video/";
    public string CreateStreamPath { get; set; } = "v2/video/create/stream/";
    public string StreamPathFormat { get; set; } = "v2/video/stream/{0}/";
    public string StreamListPath { get; set; } = "v2/video/stream/";
    public string ThumbnailPathFormat { get; set; } = "video/{0}/thumbnail/?client=vulp";
    public string RefreshTokenPath { get; set; } = "auth/token/refresh/";
    public string CreateStreamStatusValue { get; set; } = "WAIT";
    public string StartStreamStatusValue { get; set; } = "START";
    public string FinishStreamStatusValue { get; set; } = "END";
    public string DeleteStreamStatusValue { get; set; } = "DELETE";
    public string ContractVersion { get; set; } = "studio-v2-2026-08-06-r1";
    public bool EnablePrivateStudioApi { get; set; }
    public bool HeadlessLogin { get; set; }
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(60);
    public TimeSpan TokenExpirySkew { get; set; } = TimeSpan.FromMinutes(1);
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    public Action<string>? StatusCallback { get; set; }
    public Func<RutubeClientOptions, IInteractiveAuthenticator>? AuthenticatorFactory { get; set; }

    internal Func<HttpMessageHandler>? ApiHttpMessageHandlerFactory { get; set; }
    internal Func<HttpMessageHandler>? UploadHttpMessageHandlerFactory { get; set; }
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal Uri StudioApiUri => EnsureTrailingSlash(StudioApiBaseUrl);
    internal Uri PublicApiUri => EnsureTrailingSlash(PublicApiBaseUrl);

    private static Uri EnsureTrailingSlash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Base URL is required.");
        if (!value.EndsWith('/')) value += "/";
        return new Uri(value, UriKind.Absolute);
    }
}
