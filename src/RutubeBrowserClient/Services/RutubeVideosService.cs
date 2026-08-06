namespace RutubeBrowserClient;

public sealed class RutubeVideosService
{
    private readonly RutubeClient _client;
    internal RutubeVideosService(RutubeClient client) => _client = client;

    public async Task<RutubeVideo> UploadAsync(
        RutubeUploadSource source,
        RutubeVideoCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        var fields = Metadata(request.Title, request.Description, request.CategoryId, request.IsHidden, request.IsAdult, request.ScheduledAt);
        if (!string.IsNullOrWhiteSpace(request.ClientReference)) fields["client_reference"] = request.ClientReference;
        using var document = await api.PostMultipartAsync(api.Public(_client.Options.VideoPath), fields, "video_file", source,
            "video.upload", cancellationToken, outcomeUnknownOnTransportFailure: !string.IsNullOrWhiteSpace(request.ClientReference),
            clientReference: request.ClientReference, idempotencyKey: request.ClientReference).ConfigureAwait(false);
        return RutubeModelParser.Video(document.RootElement);
    }

    public async Task<RutubeVideo> GetAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.GetPublicAsync(VideoPath(videoId), "video.get", cancellationToken).ConfigureAwait(false);
        return RutubeModelParser.Video(document.RootElement);
    }

    public async Task<RutubeVideo> UpdateAsync(string videoId, RutubeVideoUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.PatchPublicAsync(VideoPath(videoId), MetadataJson(request.Title, request.Description, request.CategoryId,
            request.IsHidden, request.IsAdult, request.ScheduledAt), "video.update", cancellationToken).ConfigureAwait(false);
        return RutubeModelParser.Video(document.RootElement);
    }

    public async Task DeleteAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var _ = await api.DeletePublicAsync(VideoPath(videoId), "video.delete", cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri?> UploadThumbnailAsync(string videoId, RutubeUploadSource image, CancellationToken cancellationToken = default)
    {
        ValidateThumbnail(image);
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        api.EnsurePrivateApiEnabled();
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, _client.Options.ThumbnailPathFormat, Segment(videoId));
        using var document = await api.PostMultipartAsync(api.Studio(path), new Dictionary<string, string?>(), "file", image,
            "video.thumbnail", cancellationToken).ConfigureAwait(false);
        return JsonLookup.Uri(document.RootElement, "thumbnail_url", "url", "picture_url");
    }

    internal static void ValidateThumbnail(RutubeUploadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(source), "Rutube thumbnails must not exceed 1 MiB.");
        if (source.ContentType is not ("image/jpeg" or "image/png"))
            throw new ArgumentException("Rutube thumbnails must be JPEG or PNG.", nameof(source));
    }

    internal static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Provider id is required.", nameof(value));
        if (value.Contains('/') || value.Contains('\\')) throw new ArgumentException("Provider id is not a path.", nameof(value));
        return Uri.EscapeDataString(value);
    }

    private string VideoPath(string id) => _client.Options.VideoPath.TrimEnd('/') + "/" + Segment(id) + "/";

    private static Dictionary<string, object?> MetadataJson(string title, string description, string categoryId, bool hidden, bool adult, DateTimeOffset? scheduled) => new()
    {
        ["title"] = title,
        ["description"] = description,
        ["category"] = categoryId,
        ["is_hidden"] = hidden,
        ["is_adult"] = adult,
        ["publish_at"] = scheduled?.UtcDateTime.ToString("O")
    };

    private static Dictionary<string, string?> Metadata(string title, string description, string categoryId, bool hidden, bool adult, DateTimeOffset? scheduled) => new()
    {
        ["title"] = title,
        ["description"] = description,
        ["category"] = categoryId,
        ["is_hidden"] = hidden ? "true" : "false",
        ["is_adult"] = adult ? "true" : "false",
        ["publish_at"] = scheduled?.UtcDateTime.ToString("O")
    };
}
