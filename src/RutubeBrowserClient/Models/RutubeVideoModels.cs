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
