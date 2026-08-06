namespace RutubeBrowserClient;

public sealed class RutubeCategoriesService
{
    private readonly RutubeClient _client;
    internal RutubeCategoriesService(RutubeClient client) => _client = client;

    public async Task<IReadOnlyList<RutubeCategory>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var api = await _client.RequireApiAsync(cancellationToken).ConfigureAwait(false);
        api.EnsurePrivateApiEnabled();
        using var document = await api.GetStudioAsync(_client.Options.CategoriesPath, "categories.list", cancellationToken).ConfigureAwait(false);
        return JsonLookup.Items(document.RootElement)
            .Select(item => new RutubeCategory(
                JsonLookup.String(item, "id", "category_id") ?? "",
                JsonLookup.String(item, "title", "name") ?? "",
                JsonLookup.Bool(item, "is_active", "active") ?? true))
            .Where(x => x.Id.Length > 0 && x.Title.Length > 0)
            .ToArray();
    }
}
