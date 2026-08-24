using System.Text.Json;

namespace RutubeBrowserClient;

internal static class RutubeModelParser
{
    public static RutubeVideo Video(JsonElement root)
    {
        root = JsonLookup.Unwrap(root);
        var id = JsonLookup.String(root, "video_id", "id", "uuid") ?? throw Contract("video", "video id");
        return new RutubeVideo(
            id,
            JsonLookup.String(root, "owner_id", "author_id", "user_id"),
            JsonLookup.String(root, "title", "name") ?? "",
            JsonLookup.String(root, "description") ?? "",
            JsonLookup.String(root, "category_id", "category"),
            JsonLookup.Bool(root, "is_hidden", "hidden") ?? false,
            JsonLookup.String(root, "status", "video_status") ?? "unknown",
            JsonLookup.Uri(root, "video_url", "source_url", "url"),
            JsonLookup.Uri(root, "embed_url", "embed"),
            JsonLookup.Uri(root, "thumbnail_url", "thumbnail", "picture_url"));
    }

    public static RutubeLiveStream Live(JsonElement root)
    {
        root = JsonLookup.Unwrap(root);
        var id = JsonLookup.String(root, "video_id", "stream_id", "id", "uuid", "video") ?? throw Contract("live", "stream id");
        var providerStatus = JsonLookup.String(root, "stream_status", "status", "video_status");
        JsonElement ingestElement = root;
        var candidate = JsonLookup.Find(root, "stream") ?? JsonLookup.Find(root, "ingest");
        if (candidate is { ValueKind: JsonValueKind.Object }) ingestElement = candidate.Value;
        var ingestUrl = JsonLookup.Uri(ingestElement, "rtmps_url", "rtmp_url", "stream_url", "url", "server");
        var inputServers = JsonLookup.Find(root, "input_servers");
        if (ingestUrl is null && inputServers is { ValueKind: JsonValueKind.Object })
            ingestUrl = JsonLookup.Uri(inputServers.Value, "primary");
        var key = JsonLookup.String(ingestElement, "stream_key", "key", "broadcast_key", "input_key_gen");
        RutubeIngest? ingest = ingestUrl is null && string.IsNullOrWhiteSpace(key) ? null : new RutubeIngest
        {
            Url = ingestUrl,
            StreamKey = key,
            IsPermanent = JsonLookup.Bool(ingestElement, "is_permanent", "permanent") ??
                string.Equals(JsonLookup.String(ingestElement, "stream_key_type", "key_type"), "permanent", StringComparison.OrdinalIgnoreCase),
            ExpiresAt = JsonLookup.Date(ingestElement, "key_expires_at", "expires_at")
        };
        return new RutubeLiveStream
        {
            Id = id,
            OwnerId = JsonLookup.String(root, "owner_id", "author_id", "user_id"),
            ClientReference = JsonLookup.String(root, "client_reference", "external_id"),
            Title = JsonLookup.String(root, "title", "name") ?? "",
            Description = JsonLookup.String(root, "description") ?? "",
            CategoryId = JsonLookup.String(root, "category_id", "category"),
            Visibility = string.Equals(JsonLookup.String(root, "access_status"), "private", StringComparison.OrdinalIgnoreCase)
                || (JsonLookup.Bool(root, "is_hidden", "hidden") ?? false)
                    ? RutubeLiveVisibility.LinkOnly
                    : RutubeLiveVisibility.Public,
            Status = MapStatus(providerStatus),
            ProviderStatus = providerStatus,
            PlannedStartTime = JsonLookup.Date(root, "planned_start_time", "scheduled_at"),
            StartedAt = JsonLookup.Date(root, "started_at", "actual_start_time"),
            FinishedAt = JsonLookup.Date(root, "finished_at", "ended_at", "actual_finish_time"),
            AutoStart = JsonLookup.Bool(root, "push_auto_start", "auto_start") ?? false,
            Ingest = ingest,
            PlaybackUrl = JsonLookup.Uri(root, "source_url", "video_url", "public_url", "web_url"),
            EmbedUrl = JsonLookup.Uri(root, "embed_url", "embed"),
            ThumbnailUrl = JsonLookup.Uri(root, "thumbnail_url", "thumbnail", "picture_url"),
            SignalPresent = JsonLookup.Bool(root, "signal_present", "has_signal", "is_signal") ??
                string.Equals(providerStatus, "actual", StringComparison.OrdinalIgnoreCase)
        };
    }

    public static RutubeLiveStreamStatus MapStatus(string? status)
    {
        var normalized = status?.Trim().Replace('-', '_').ToUpperInvariant();
        return normalized switch
        {
            "WAIT" or "WAITING" or "SCHEDULED" => RutubeLiveStreamStatus.Waiting,
            "READY" or "PREPARED" or "IDLE" => RutubeLiveStreamStatus.Prepared,
            "START" or "STARTED" or "RUNNING" or "LIVE" or "ONLINE" or "ACTUAL" => RutubeLiveStreamStatus.Live,
            "END" or "ENDED" or "FINISHED" or "STOPPED" or "DONE" => RutubeLiveStreamStatus.Finished,
            "PROCESSING" or "CONVERTING" => RutubeLiveStreamStatus.Processing,
            "PUBLISHED" or "COMPLETED" or "VOD" => RutubeLiveStreamStatus.Ready,
            "FAILED" or "ERROR" or "REJECTED" or "DISABLE" or "FIN_ERR" => RutubeLiveStreamStatus.Failed,
            "DELETED" or "REMOVED" => RutubeLiveStreamStatus.Deleted,
            _ => RutubeLiveStreamStatus.Unknown
        };
    }

    private static RutubeApiException Contract(string operation, string field) =>
        new(operation + ".parse", System.Net.HttpStatusCode.OK, "contract_drift", $"Rutube response did not contain {field}.");
}
