using System.Globalization;
using System.Net;

namespace RutubeBrowserClient;

/// <summary>
/// Typed adapter over Rutube Studio's private live contract. Call <see cref="ProbeCapabilityAsync"/>
/// and run a private/link-only canary before enabling it in production.
/// </summary>
public sealed class RutubeLiveService
{
    private readonly RutubeClient _client;
    internal RutubeLiveService(RutubeClient client) => _client = client;

    public async Task<RutubeStudioCapability> ProbeCapabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
            api.EnsurePrivateApiEnabled();
            var query = _client.Options.StreamListPath + (_client.Options.StreamListPath.Contains('?') ? "&" : "?") +
                "stream_status=wait&page=1&per_page=1";
            using var _ = await api.GetStudioAsync(query, "live.capability", cancellationToken).ConfigureAwait(false);
            return new(true, _client.ContractVersion, _client.AccountId, "Rutube Studio live endpoint responded successfully.");
        }
        catch (RutubeClientException ex)
        {
            return new(false, _client.ContractVersion, _client.AccountId, SafeText.Sanitize(ex.Message, 300));
        }
    }

    public async Task<RutubeLiveStream> CreateAsync(RutubeLiveCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["stream_status"] = _client.Options.CreateStreamStatusValue,
            ["title"] = request.Title,
            ["description"] = request.Description,
            ["category"] = request.CategoryId,
            ["is_adult"] = request.IsAdult,
            ["is_hidden"] = request.Visibility == RutubeLiveVisibility.LinkOnly,
            ["planned_start_time"] = request.PlannedStartTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };
        using var document = await api.PostStudioAsync(_client.Options.CreateStreamPath, payload, "live.create", cancellationToken,
            outcomeUnknownOnTransportFailure: true, clientReference: request.ClientReference, idempotencyKey: request.ClientReference)
            .ConfigureAwait(false);
        return RutubeModelParser.Live(document.RootElement);
    }

    public async Task<RutubeLiveStream> GetAsync(string streamId, CancellationToken cancellationToken = default)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.GetStudioAsync(StreamPath(streamId), "live.get", cancellationToken).ConfigureAwait(false);
        return RutubeModelParser.Live(document.RootElement);
    }

    public Task<RutubeLiveStream> UpdateAsync(string streamId, RutubeLiveUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return TransitionAsync(streamId, new Dictionary<string, object?>
        {
            ["title"] = request.Title,
            ["description"] = request.Description,
            ["category"] = request.CategoryId,
            ["is_adult"] = request.IsAdult,
            ["is_hidden"] = request.Visibility == RutubeLiveVisibility.LinkOnly,
            ["planned_start_time"] = request.PlannedStartTime?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        }, "live.update", cancellationToken);
    }

    public Task<RutubeLiveStream> StartAsync(string streamId, CancellationToken cancellationToken = default) =>
        TransitionAsync(streamId, new { access_status = _client.Options.StartAccessStatusValue }, "live.start", cancellationToken);

    public Task<RutubeLiveStream> FinishAsync(string streamId, CancellationToken cancellationToken = default) =>
        TransitionAsync(streamId, new { stream_status = _client.Options.FinishStreamStatusValue }, "live.finish", cancellationToken);

    /// <summary>
    /// Enables the account's existing permanent stream key for this stream without generating or rotating it.
    /// </summary>
    public async Task<RutubeLiveStream> UsePermanentStreamKeyAsync(
        string streamId,
        CancellationToken cancellationToken = default)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var path = string.Format(CultureInfo.InvariantCulture, _client.Options.PermanentStreamKeyPathFormat,
            RutubeVideosService.Segment(streamId));
        var payload = new Dictionary<string, object?>
        {
            ["is_active"] = true
        };
        using var _ = await api.PostStudioAsync(path, payload, "live.use-permanent-key", cancellationToken).ConfigureAwait(false);
        return await GetAsync(streamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RutubeLiveStream> RotateStreamKeyAsync(
        string streamId,
        RutubeStreamKeyMode mode,
        CancellationToken cancellationToken = default)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var path = string.Format(CultureInfo.InvariantCulture, _client.Options.PermanentStreamKeyPathFormat,
            RutubeVideosService.Segment(streamId));
        object payload = mode == RutubeStreamKeyMode.Permanent
            ? new Dictionary<string, object?> { ["is_active"] = true, ["new_key"] = true }
            : new Dictionary<string, object?> { ["is_active"] = false };
        using var _ = await api.PostStudioAsync(path, payload, "live.rotate-key", cancellationToken).ConfigureAwait(false);
        return await GetAsync(streamId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string streamId, CancellationToken cancellationToken = default)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        using var _ = await api.PostStudioAsync(StreamPath(streamId),
            new { stream_status = _client.Options.DeleteStreamStatusValue }, "live.delete", cancellationToken).ConfigureAwait(false);
    }

    public async Task<Uri?> UploadThumbnailAsync(string streamId, RutubeUploadSource image, CancellationToken cancellationToken = default)
    {
        RutubeVideosService.ValidateThumbnail(image);
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var path = string.Format(CultureInfo.InvariantCulture, _client.Options.ThumbnailPathFormat, RutubeVideosService.Segment(streamId));
        using var document = await api.PostMultipartAsync(api.Studio(path), new Dictionary<string, string?>(), "file", image,
            "live.thumbnail", cancellationToken).ConfigureAwait(false);
        return JsonLookup.Uri(document.RootElement, "thumbnail_url", "url", "picture_url");
    }

    /// <summary>Finds the provider object after an ambiguous create. It never creates or retries a stream.</summary>
    public async Task<RutubeLiveStream?> ReconcileOwnedAsync(
        RutubeLiveReconcileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequestValidation.ClientReference(request.ClientReference, required: true);
        if (string.IsNullOrWhiteSpace(request.OwnerId)) throw new ArgumentException("Owner id is required.", nameof(request));
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        var query = _client.Options.StreamListPath + (_client.Options.StreamListPath.Contains('?') ? "&" : "?") +
            "stream_status=actual,wait,done,disable,fin_err&page=1&per_page=100";
        using var document = await api.GetStudioAsync(query, "live.reconcile", cancellationToken).ConfigureAwait(false);
        var matches = JsonLookup.Items(document.RootElement)
            .Select(RutubeModelParser.Live)
            .Where(stream => (string.IsNullOrWhiteSpace(stream.OwnerId)
                    || string.Equals(stream.OwnerId, request.OwnerId, StringComparison.Ordinal))
                && (string.Equals(stream.ClientReference, request.ClientReference, StringComparison.Ordinal)
                    || MatchesFallback(stream, request)))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new RutubeApiException("live.reconcile", HttpStatusCode.Conflict, "ambiguous_reconciliation",
                "More than one owned stream matched the immutable reconciliation fields.")
        };
    }

    private async Task<RutubeLiveStream> TransitionAsync(string streamId, object payload, string operation, CancellationToken cancellationToken)
    {
        var api = await PrivateApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.PostStudioAsync(StreamPath(streamId), payload, operation, cancellationToken).ConfigureAwait(false);
        try { return RutubeModelParser.Live(document.RootElement); }
        catch (RutubeApiException ex) when (ex.ErrorCode == "contract_drift")
        {
            return await GetAsync(streamId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RutubeStudioApi> PrivateApiAsync(CancellationToken cancellationToken)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        api.EnsurePrivateApiEnabled();
        return api;
    }

    private string StreamPath(string id) => string.Format(CultureInfo.InvariantCulture,
        _client.Options.StreamPathFormat, RutubeVideosService.Segment(id));

    private static bool MatchesFallback(RutubeLiveStream stream, RutubeLiveReconcileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || !string.Equals(stream.Title, request.Title, StringComparison.Ordinal)) return false;
        if (request.PlannedStartTime is null) return false;
        return stream.PlannedStartTime is { } actual && Math.Abs((actual - request.PlannedStartTime.Value).TotalMinutes) <= 2;
    }
}
