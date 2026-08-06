namespace RutubeBrowserClient;

public sealed class RutubeIdentityService
{
    private readonly RutubeClient _client;
    internal RutubeIdentityService(RutubeClient client) => _client = client;

    public async Task<RutubeIdentity> GetAsync(CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        using var document = await api.GetStudioAsync(_client.Options.IdentityPath, "identity.get", cancellationToken).ConfigureAwait(false);
        var root = JsonLookup.Unwrap(document.RootElement);
        var id = JsonLookup.String(root, "account_id", "user_id", "owner_id", "id")?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new RutubeApiException("identity.get", System.Net.HttpStatusCode.OK, "contract_drift", "Identity response did not contain an account id.");
        var identity = new RutubeIdentity(
            id,
            JsonLookup.String(root, "username", "login"),
            JsonLookup.String(root, "display_name", "name", "title"),
            JsonLookup.String(root, "channel_id", "channel"),
            JsonLookup.Bool(root, "phone_verified", "is_phone_confirmed", "phone_confirmed") ?? false);
        await _client.UpdateIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }
}
