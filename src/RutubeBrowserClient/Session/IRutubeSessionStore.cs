namespace RutubeBrowserClient;

/// <summary>Durable storage for a sensitive Rutube browser session.</summary>
public interface IRutubeSessionStore
{
    Task<RutubeSession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RutubeSession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
