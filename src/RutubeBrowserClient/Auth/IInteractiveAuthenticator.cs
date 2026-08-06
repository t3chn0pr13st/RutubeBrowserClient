namespace RutubeBrowserClient;

public interface IInteractiveAuthenticator
{
    Task<RutubeSession> AuthenticateAsync(CancellationToken cancellationToken = default);
}
