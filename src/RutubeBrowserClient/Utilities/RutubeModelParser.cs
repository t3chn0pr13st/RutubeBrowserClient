using System.Text.Json;

namespace RutubeBrowserClient;

internal static class RutubeModelParser
{
    public static RutubeVideo Video(JsonElement root)
    {
        root = JsonLookup.Unwrap(root);
        var id = JsonLookup.String(root, "video_id", "id", "uuid") ?? throw Contract("video", "video id");
        var action = JsonLookup.Find(root, "action_reason");
        var actionId = action is { ValueKind: JsonValueKind.Object } ? JsonLookup.Int64(action.Value, "id") : null;
        var actionName = action is { ValueKind: JsonValueKind.Object } ? JsonLookup.String(action.Value, "name", "slug") : null;
        var status = JsonLookup.String(root, "status", "video_status", "displayed_status") ?? actionId switch
        {
            0 => "ready",
            32 => "moderation",
            5 or 15 or 16 or 20 => "processing",
            1 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 10 or 11 or 17 or 21 or 22 => "failed",
            _ => actionName ?? "unknown"
        };
        var playbackUrl = JsonLookup.Uri(root, "video_url", "source_url", "url");
        var embedUrl = EnsurePrivateEmbedUrl(id, playbackUrl, JsonLookup.Uri(root, "embed_url", "embed"));
        return new RutubeVideo(
            id,
            JsonLookup.String(root, "owner_id", "author_id", "user_id"),
            JsonLookup.String(root, "title", "name") ?? "",
            JsonLookup.String(root, "description") ?? "",
            JsonLookup.String(root, "category_id", "category"),
            JsonLookup.Bool(root, "is_hidden", "hidden") ?? false,
            status,
            playbackUrl,
            embedUrl,
            JsonLookup.Uri(root, "thumbnail_url", "thumbnail", "picture_url"));
    }

    private static Uri? EnsurePrivateEmbedUrl(string id, Uri? playbackUrl, Uri? embedUrl)
    {
        if (playbackUrl is null || !IsRutubeHost(playbackUrl.IdnHost))
            return embedUrl;
        var privateKey = QueryValue(playbackUrl, "p");
        if (string.IsNullOrWhiteSpace(privateKey)) return embedUrl;
        if (embedUrl is null)
            embedUrl = new Uri($"https://rutube.ru/play/embed/{Uri.EscapeDataString(id)}/", UriKind.Absolute);
        if (!IsRutubeHost(embedUrl.IdnHost) || !string.IsNullOrWhiteSpace(QueryValue(embedUrl, "p")))
            return embedUrl;
        var builder = new UriBuilder(embedUrl);
        var retained = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(retained)
            ? $"p={Uri.EscapeDataString(privateKey)}"
            : $"{retained}&p={Uri.EscapeDataString(privateKey)}";
        return builder.Uri;
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = pair.Split('=', 2);
            if (Uri.UnescapeDataString(pieces[0]).Equals(name, StringComparison.OrdinalIgnoreCase))
                return pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : "";
        }
        return null;
    }

    private static bool IsRutubeHost(string host) =>
        host.Equals("rutube.ru", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".rutube.ru", StringComparison.OrdinalIgnoreCase);

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
                string.Equals(providerStatus, "actual", StringComparison.OrdinalIgnoreCase),
            // Studio has returned different spellings across front-end releases.
            // Keep the aliases narrow and numeric so a boolean "online" flag is
            // never mistaken for a viewer count.
            CurrentViewers = NonNegative(JsonLookup.Int64(root,
                "current_viewers", "viewers_online", "online_viewers", "viewers_count", "viewers")),
            TotalViews = NonNegative(JsonLookup.Int64(root,
                "hits", "views", "view_count", "views_count", "total_views"))
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

    private static long? NonNegative(long? value) => value is { } number ? Math.Max(0, number) : null;

    private static RutubeApiException Contract(string operation, string field) =>
        new(operation + ".parse", System.Net.HttpStatusCode.OK, "contract_drift", $"Rutube response did not contain {field}.");
}
