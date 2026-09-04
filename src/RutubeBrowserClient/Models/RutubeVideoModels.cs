namespace RutubeBrowserClient;

public sealed class RutubeVideoCreateRequest
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string CategoryId { get; init; }
    public bool IsHidden { get; init; }
    public bool IsAdult { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public string? ClientReference { get; init; }

    internal void Validate()
    {
        RequestValidation.Metadata(Title, Description, CategoryId);
        RequestValidation.ClientReference(ClientReference);
    }
}

public sealed class RutubeVideoUpdateRequest
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string CategoryId { get; init; }
    public bool IsHidden { get; init; }
    public bool IsAdult { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    internal void Validate() => RequestValidation.Metadata(Title, Description, CategoryId);
}

public sealed record RutubeVideo(
    string Id,
    string? OwnerId,
    string Title,
    string Description,
    string? CategoryId,
    bool IsHidden,
    string Status,
    Uri? PlaybackUrl,
    Uri? EmbedUrl,
    Uri? ThumbnailUrl);

public enum RutubeVideoUploadStage
{
    Created,
    MetadataReady,
    TransferReady,
    Uploaded
}

/// <summary>
/// Restart-safe state for Rutube Studio's upload-session plus TUS transfer.
/// Persist this value in protected storage between stages; <see cref="ToString"/>
/// intentionally omits the resumable upload URL.
/// </summary>
public sealed record RutubeVideoUploadSession
{
    public required string VideoId { get; init; }
    public required string SessionId { get; init; }
    public required string BatchId { get; init; }
    public required string UserId { get; init; }
    public required long Length { get; init; }
    public required RutubeVideoUploadStage Stage { get; init; }
    public string? UploadUrl { get; init; }
    public long Offset { get; init; }

    public override string ToString() => $"RutubeVideoUploadSession(VideoId={VideoId}, Stage={Stage}, Offset={Offset}, Length={Length})";
}

public sealed record RutubeVideoUploadProgress(
    string VideoId,
    string State,
    double? Progress,
    int? ActionReasonId,
    string? ActionReasonName)
{
    public bool IsProcessing => State is "receiving" or "moving" or "processing";
    public bool IsReady => !IsProcessing && ActionReasonId is 0 or 32;
    public bool IsFailed => State is "error" or "failed" || ActionReasonId is 1 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 10 or 11 or 17 or 21 or 22;
}
