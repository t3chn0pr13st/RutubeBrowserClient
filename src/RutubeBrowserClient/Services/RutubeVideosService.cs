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
        var session = await CreateUploadSessionAsync(source, request, cancellationToken).ConfigureAwait(false);
        session = await PrepareUploadAsync(session, request, cancellationToken).ConfigureAwait(false);
        session = await BeginUploadAsync(session, source, request.ClientReference, cancellationToken).ConfigureAwait(false);
        session = await UploadAsync(session, source, cancellationToken).ConfigureAwait(false);
        return await GetPrivateAsync(session.VideoId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RutubeVideoUploadSession> CreateUploadSessionAsync(
        RutubeUploadSource source,
        RutubeVideoCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var reference = request.ClientReference ?? Guid.NewGuid().ToString("N");
        var batchId = StableBatchId(reference);
        var createPath = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            _client.Options.CreateVideoUploadSessionPath, Uri.EscapeDataString(batchId));
        using var created = await api.PostStudioAsync(createPath, new { title = request.Title }, "video.upload.create-session",
            cancellationToken, outcomeUnknownOnTransportFailure: true, clientReference: reference,
            idempotencyKey: reference).ConfigureAwait(false);
        var videoId = JsonLookup.String(created.RootElement, "video", "video_id", "id")
            ?? throw Contract("video.upload.create-session", "video id");
        var sessionId = JsonLookup.String(created.RootElement, "sid", "session_id")
            ?? throw Contract("video.upload.create-session", "session id");
        var userId = _client.AccountId;
        if (string.IsNullOrWhiteSpace(userId)) throw Contract("video.upload.create-session", "authenticated user id");
        return new RutubeVideoUploadSession
        {
            VideoId = videoId,
            SessionId = sessionId,
            BatchId = batchId,
            UserId = userId,
            Length = source.Length,
            Stage = RutubeVideoUploadStage.Created
        };
    }

    public async Task<RutubeVideoUploadSession> PrepareUploadAsync(
        RutubeVideoUploadSession session,
        RutubeVideoCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (session.Stage is RutubeVideoUploadStage.MetadataReady or RutubeVideoUploadStage.TransferReady or RutubeVideoUploadStage.Uploaded)
            return session;
        if (session.Stage != RutubeVideoUploadStage.Created)
            throw new ArgumentException("Rutube upload session is not ready for metadata.", nameof(session));
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var updatePath = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            _client.Options.UpdateVideoPathFormat, Segment(session.VideoId));
        using var _ = await api.PatchStudioAsync(updatePath,
            MetadataJson(request.Title, request.Description, request.CategoryId, request.IsHidden, request.IsAdult, request.ScheduledAt),
            "video.upload.metadata", cancellationToken).ConfigureAwait(false);
        return session with { Stage = RutubeVideoUploadStage.MetadataReady };
    }

    public async Task<RutubeVideoUploadSession> BeginUploadAsync(
        RutubeVideoUploadSession session,
        RutubeUploadSource source,
        string? clientReference = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUploadSession(session, source);
        if (session.Stage is RutubeVideoUploadStage.TransferReady or RutubeVideoUploadStage.Uploaded)
            return session;
        if (session.Stage != RutubeVideoUploadStage.MetadataReady)
            throw new ArgumentException("Rutube upload metadata is not ready.", nameof(session));
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = api.Upload(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            _client.Options.TusUploadPathFormat, Segment(session.SessionId)));
        var fingerprint = StableFingerprint(session, source);
        var uploadUrl = await api.CreateTusUploadAsync(endpoint, source.Length, new Dictionary<string, string>
        {
            ["sessionId"] = session.SessionId,
            ["videoId"] = session.VideoId,
            ["userId"] = session.UserId,
            ["currentFingerprint"] = fingerprint,
            ["remoteFingerprint"] = fingerprint
        }, clientReference ?? session.BatchId, cancellationToken).ConfigureAwait(false);
        return session with { UploadUrl = uploadUrl.ToString(), Offset = 0, Stage = RutubeVideoUploadStage.TransferReady };
    }

    public async Task<RutubeVideoUploadSession> UploadAsync(
        RutubeVideoUploadSession session,
        RutubeUploadSource source,
        CancellationToken cancellationToken = default)
    {
        ValidateUploadSession(session, source);
        if (session.Stage == RutubeVideoUploadStage.Uploaded) return session;
        if (session.Stage != RutubeVideoUploadStage.TransferReady ||
            !Uri.TryCreate(session.UploadUrl, UriKind.Absolute, out var uploadUrl))
            throw new ArgumentException("Rutube upload session is not ready for transfer.", nameof(session));
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var offset = await api.GetTusOffsetAsync(uploadUrl, cancellationToken).ConfigureAwait(false);
        if (offset > source.Length) throw Contract("video.upload.tus-head", "valid upload offset");
        // Rutube's current TUS gateway treats the end of a successful PATCH as
        // completion, even when Upload-Length is larger. Match Studio's tus-js
        // client and send all remaining bytes in one request. If the transport
        // is interrupted, the durable session can still resume from HEAD's
        // remote offset on the next invocation.
        if (offset < source.Length)
            offset = await api.PatchTusAsync(uploadUrl, source, offset, source.Length - offset, cancellationToken).ConfigureAwait(false);
        return session with { Offset = offset, Stage = RutubeVideoUploadStage.Uploaded };
    }

    public async Task<RutubeVideoUploadProgress> GetUploadProgressAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            _client.Options.UploadProgressPathFormat, Segment(videoId));
        using var document = await api.GetStudioAsync(path, "video.upload.progress", cancellationToken).ConfigureAwait(false);
        var root = JsonLookup.Unwrap(document.RootElement);
        var progressElement = JsonLookup.Find(root, "progress");
        var state = progressElement is { ValueKind: System.Text.Json.JsonValueKind.Object }
            ? JsonLookup.String(progressElement.Value, "state") ?? "processing"
            : "complete";
        double? progress = null;
        if (progressElement is { ValueKind: System.Text.Json.JsonValueKind.Object } &&
            JsonLookup.Find(progressElement.Value, "progress") is { } value && value.TryGetDouble(out var number)) progress = number;
        var action = JsonLookup.Find(root, "action_reason");
        var actionId = action is { ValueKind: System.Text.Json.JsonValueKind.Object }
            ? (int?)JsonLookup.Int64(action.Value, "id")
            : null;
        var actionName = action is { ValueKind: System.Text.Json.JsonValueKind.Object }
            ? JsonLookup.String(action.Value, "name", "slug")
            : null;
        return new RutubeVideoUploadProgress(videoId, state.Trim().ToLowerInvariant(), progress, actionId, actionName);
    }

    public async Task<RutubeVideo> GetPrivateAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            _client.Options.PrivateVideoPathFormat, Segment(videoId));
        using var document = await api.GetStudioAsync(path, "video.get-private", cancellationToken).ConfigureAwait(false);
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
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            _client.Options.DeleteVideoPathFormat, Segment(videoId));
        using var _ = await api.DeleteStudioAsync(path, "video.delete", cancellationToken).ConfigureAwait(false);
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

    private async Task<RutubeStudioApi> PrivateApiAsync(CancellationToken cancellationToken)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        api.EnsurePrivateApiEnabled();
        return api;
    }

    private static string StableBatchId(string reference)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reference));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string StableFingerprint(RutubeVideoUploadSession session, RutubeUploadSource source)
    {
        var canonical = $"{session.BatchId}|{session.VideoId}|{source.FileName}|{source.Length}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..20];
    }

    private static void ValidateUploadSession(RutubeVideoUploadSession session, RutubeUploadSource source)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(source);
        if (session.Length != source.Length) throw new ArgumentException("Upload source length does not match the Rutube upload session.", nameof(source));
        _ = Segment(session.VideoId);
        _ = Segment(session.SessionId);
        if (string.IsNullOrWhiteSpace(session.BatchId) || string.IsNullOrWhiteSpace(session.UserId))
            throw new ArgumentException("Rutube upload session is incomplete.", nameof(session));
    }

    private static RutubeApiException Contract(string operation, string field) =>
        new(operation, System.Net.HttpStatusCode.OK, "contract_drift", $"Rutube response did not contain {field}.");

    private static Dictionary<string, object?> MetadataJson(string title, string description, string categoryId, bool hidden, bool adult, DateTimeOffset? scheduled) => new()
    {
        ["title"] = title,
        ["description"] = description,
        ["category"] = categoryId,
        ["is_hidden"] = hidden,
        ["is_adult"] = adult,
        ["publish_at"] = scheduled?.UtcDateTime.ToString("O")
    };

}
