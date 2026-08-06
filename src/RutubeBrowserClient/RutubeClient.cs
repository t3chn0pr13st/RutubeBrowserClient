using System.Net;

namespace RutubeBrowserClient;

/// <summary>Typed facade for an authenticated Rutube Studio browser session.</summary>
public sealed class RutubeClient : IAsyncDisposable
{
    private readonly RutubeClientOptions _options;
    private readonly IRutubeSessionStore _store;
    private readonly IInteractiveAuthenticator _authenticator;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private RutubeSession? _session;
    private RutubeStudioApi? _api;
    private RutubeIdentityService? _identity;
    private RutubeCategoriesService? _categories;
    private RutubeVideosService? _videos;
    private RutubeLiveService? _live;

    public RutubeClient(IRutubeSessionStore sessionStore, RutubeClientOptions? options = null)
    {
        _store = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _options = options ?? new RutubeClientOptions();
        _authenticator = _options.AuthenticatorFactory?.Invoke(_options) ?? new PlaywrightAuthenticator(_options);
    }

    public static RutubeClient Create(string sessionFilePath, Action<RutubeClientOptions>? configure = null)
    {
        var options = new RutubeClientOptions();
        configure?.Invoke(options);
        return new RutubeClient(new FileRutubeSessionStore(sessionFilePath, options.StatusCallback), options);
    }

    public RutubeIdentityService Identity => _identity ??= new(this);
    public RutubeCategoriesService Categories => _categories ??= new(this);
    public RutubeVideosService Videos => _videos ??= new(this);
    public RutubeLiveService Live => _live ??= new(this);
    public string? AccountId => _session?.AccountId;
    public string ContractVersion => _options.ContractVersion;

    /// <summary>Loads or interactively creates a session and verifies it against the identity endpoint.</summary>
    public async Task<RutubeIdentity> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_session is null || !_session.HasCredentials)
                await InteractiveLoginCoreAsync(cancellationToken).ConfigureAwait(false);
            else RebuildApi();

            try
            {
                return await VerifyIdentityCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RutubeApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _options.StatusCallback?.Invoke("Saved Rutube session expired; interactive sign-in is required.");
                await InteractiveLoginCoreAsync(cancellationToken).ConfigureAwait(false);
                return await VerifyIdentityCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _authLock.Release(); }
    }

    public async Task ExportSessionAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionForExportAsync(cancellationToken).ConfigureAwait(false);
        await new FileRutubeSessionStore(destinationPath, _options.StatusCallback).SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportSessionToBase64Async(CancellationToken cancellationToken = default) =>
        RutubeSessionSerializer.ToBase64(await GetSessionForExportAsync(cancellationToken).ConfigureAwait(false));

    public async Task ImportSessionAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var session = await new FileRutubeSessionStore(sourcePath).LoadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || !session.HasCredentials) throw new RutubeClientException("Imported file has no usable Rutube credentials.");
        await AdoptSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public Task ImportSessionFromBase64Async(string payload, CancellationToken cancellationToken = default)
    {
        var session = RutubeSessionSerializer.FromBase64(payload);
        if (!session.HasCredentials) throw new RutubeClientException("Imported payload has no usable Rutube credentials.");
        return AdoptSessionAsync(session, cancellationToken);
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        _api?.Dispose();
        _api = null;
        _session = null;
        await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    internal RutubeClientOptions Options => _options;

    internal async Task<RutubeStudioApi> RequireApiAsync(CancellationToken cancellationToken)
    {
        if (_api is not null) return _api;
        _session ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_session is null || !_session.HasCredentials)
            throw new RutubeSessionExpiredException("No Rutube session is available. Call EnsureAuthenticatedAsync on an interactive machine or import a session.");
        RebuildApi();
        return _api!;
    }

    internal Task PersistSessionAsync(CancellationToken cancellationToken) =>
        _session is null ? Task.CompletedTask : _store.SaveAsync(_session, cancellationToken);

    internal async Task UpdateIdentityAsync(RutubeIdentity identity, CancellationToken cancellationToken)
    {
        if (_session is null) return;
        _session.AccountId = identity.AccountId;
        _session.ChannelId = identity.ChannelId;
        _session.DisplayName = identity.DisplayName;
        await _store.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
    }

    private async Task InteractiveLoginCoreAsync(CancellationToken cancellationToken)
    {
        _session = await _authenticator.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
        RebuildApi();
    }

    private async Task<RutubeIdentity> VerifyIdentityCoreAsync(CancellationToken cancellationToken)
    {
        var identity = await Identity.GetAsync(cancellationToken).ConfigureAwait(false);
        await UpdateIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    private async Task<RutubeSession> GetSessionForExportAsync(CancellationToken cancellationToken)
    {
        _session ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_session is null || !_session.HasCredentials)
            throw new RutubeClientException("No Rutube session is available for export.");
        return _session;
    }

    private async Task AdoptSessionAsync(RutubeSession session, CancellationToken cancellationToken)
    {
        _session = session;
        RebuildApi();
        await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private void RebuildApi()
    {
        _api?.Dispose();
        _api = new RutubeStudioApi(_session!, _options, PersistSessionAsync);
    }

    public ValueTask DisposeAsync()
    {
        _api?.Dispose();
        _authLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
