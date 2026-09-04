namespace RutubeBrowserClient;

/// <summary>Runtime and transport settings for <see cref="RutubeClient"/>.</summary>
public sealed class RutubeClientOptions
{
    public string WebBaseUrl { get; set; } = "https://rutube.ru";
    public string StudioBaseUrl { get; set; } = "https://studio.rutube.ru";
    public string StudioApiBaseUrl { get; set; } = "https://studio.rutube.ru/api/";
    public string PublicApiBaseUrl { get; set; } = "https://rutube.ru/api/";
    public string UploadBaseUrl { get; set; } = "https://u.rutube.ru/";
    public string LoginUrl { get; set; } = "https://studio.rutube.ru/";
    public string IdentityPath { get; set; } = "v2/accounts/visitor/?client=vulp";
    public string CategoriesPath { get; set; } = "v2/video/categories/";
    public string VideoPath { get; set; } = "video/";
    public string CreateVideoUploadSessionPath { get; set; } = "uploader/upload_session/?client=vulp&batch_id={0}";
    public string PrivateVideoPathFormat { get; set; } = "v2/video/private/{0}/?client=vulp";
    public string UpdateVideoPathFormat { get; set; } = "v2/video/{0}/?client=vl";
    public string UploadProgressPathFormat { get; set; } = "uploader/{0}/progress/";
    public string TusUploadPathFormat { get; set; } = "upload/{0}";
    public string CreateStreamPath { get; set; } = "v2/video/create/stream/";
    public string StreamPathFormat { get; set; } = "v2/video/stream/{0}/";
    public string StreamListPath { get; set; } = "v2/video/stream/owner/";
    public string PermanentStreamKeyPathFormat { get; set; } = "v1/video/stream/{0}/permkey/";
    public string ThumbnailPathFormat { get; set; } = "video/{0}/thumbnail/?client=vulp";
    public string RefreshTokenPath { get; set; } = "auth/token/refresh/";
    public string CreateStreamStatusValue { get; set; } = "wait";
    public string StartAccessStatusValue { get; set; } = "public";
    public string FinishStreamStatusValue { get; set; } = "done";
    public string DeleteStreamStatusValue { get; set; } = "deleted";
    public string ContractVersion { get; set; } = "studio-v2-2026-09-04-baldr-355";
    public bool EnablePrivateStudioApi { get; set; }
    public bool HeadlessLogin { get; set; }
    public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(60);
    public int TusChunkBytes { get; set; } = 64 * 1024 * 1024;
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
    internal Uri UploadUri => EnsureTrailingSlash(UploadBaseUrl);

    private static Uri EnsureTrailingSlash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Base URL is required.");
        if (!value.EndsWith('/')) value += "/";
        return new Uri(value, UriKind.Absolute);
    }
}
