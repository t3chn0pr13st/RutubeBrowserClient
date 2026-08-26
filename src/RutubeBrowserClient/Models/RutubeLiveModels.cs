using System.Diagnostics;

namespace RutubeBrowserClient;

public enum RutubeLiveVisibility { Public, LinkOnly }
public enum RutubeStreamKeyMode { Temporary, Permanent }
public enum RutubeLiveStreamStatus { Unknown, Waiting, Prepared, Live, Finished, Processing, Ready, Failed, Deleted }

public sealed class RutubeLiveCreateRequest
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string CategoryId { get; init; }
    public RutubeLiveVisibility Visibility { get; init; }
    public bool IsAdult { get; init; }
    public DateTimeOffset? PlannedStartTime { get; init; }
    /// <summary>Automatically starts the live session when Rutube receives the ingest signal.</summary>
    public bool AutoStart { get; init; }
    public RutubeStreamKeyMode StreamKeyMode { get; init; } = RutubeStreamKeyMode.Temporary;
    public required string ClientReference { get; init; }

    internal void Validate()
    {
        RequestValidation.Metadata(Title, Description, CategoryId);
        RequestValidation.ClientReference(ClientReference, required: true);
        if (PlannedStartTime is { } planned && planned > DateTimeOffset.UtcNow.AddDays(90))
            throw new ArgumentOutOfRangeException(nameof(PlannedStartTime), "Rutube supports schedules up to 90 days ahead.");
    }
}

public sealed class RutubeLiveUpdateRequest
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public required string CategoryId { get; init; }
    public RutubeLiveVisibility Visibility { get; init; }
    public bool IsAdult { get; init; }
    public DateTimeOffset? PlannedStartTime { get; init; }
    /// <summary>Automatically starts the live session when Rutube receives the ingest signal.</summary>
    public bool AutoStart { get; init; }
    internal void Validate() => RequestValidation.Metadata(Title, Description, CategoryId);
}

public sealed record RutubeLiveReconcileRequest(
    string OwnerId,
    string ClientReference,
    string? Title = null,
    DateTimeOffset? PlannedStartTime = null);

[DebuggerDisplay("RutubeIngest(Url={Url}, StreamKey=[REDACTED])")]
public sealed class RutubeIngest
{
    public Uri? Url { get; init; }
    public string? StreamKey { get; init; }
    public bool IsPermanent { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public override string ToString() => $"RutubeIngest(Url={Url}, StreamKey=[REDACTED])";
}

[DebuggerDisplay("RutubeLiveStream(Id={Id}, Status={Status}, StreamKey=[REDACTED])")]
public sealed class RutubeLiveStream
{
    public required string Id { get; init; }
    public string? OwnerId { get; init; }
    public string? ClientReference { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? CategoryId { get; init; }
    public RutubeLiveVisibility Visibility { get; init; }
    public RutubeLiveStreamStatus Status { get; init; }
    public string? ProviderStatus { get; init; }
    public DateTimeOffset? PlannedStartTime { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>The provider-confirmed automatic-start setting.</summary>
    public bool AutoStart { get; init; }
    public RutubeIngest? Ingest { get; init; }
    public Uri? PlaybackUrl { get; init; }
    public Uri? EmbedUrl { get; init; }
    public Uri? ThumbnailUrl { get; init; }
    public bool SignalPresent { get; init; }
    /// <summary>
    /// Текущее число одновременных зрителей. <c>null</c> означает, что
    /// наблюдавшийся Studio-ответ не содержал поддерживаемого счётчика.
    /// </summary>
    public long? CurrentViewers { get; init; }
    /// <summary>
    /// Накопленное число просмотров. Это просмотры, а не гарантированно
    /// уникальные зрители.
    /// </summary>
    public long? TotalViews { get; init; }
    public override string ToString() => $"RutubeLiveStream(Id={Id}, Status={Status}, StreamKey=[REDACTED])";
}

public sealed record RutubeStudioCapability(
    bool Available,
    string ContractVersion,
    string? AccountId,
    string SafeMessage);
