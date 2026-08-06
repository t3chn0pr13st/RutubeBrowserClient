using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RutubeBrowserClient;

public static class RutubeSessionSerializer
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(RutubeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return JsonSerializer.Serialize(session, JsonOptions);
    }

    public static RutubeSession Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new RutubeClientException("Session payload is empty.");
        try
        {
            var session = JsonSerializer.Deserialize<RutubeSession>(json, JsonOptions)
                ?? throw new RutubeClientException("Session payload is empty.");
            Normalize(session);
            return session;
        }
        catch (JsonException ex)
        {
            throw new RutubeClientException("Session payload is not valid RutubeBrowserClient JSON.", ex);
        }
    }

    public static string ToBase64(RutubeSession session) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(session)));

    public static RutubeSession FromBase64(string value)
    {
        try { return Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(value))); }
        catch (FormatException ex) { throw new RutubeClientException("Session payload is not valid base64.", ex); }
    }

    internal static void Normalize(RutubeSession session)
    {
        session.Cookies ??= [];
        session.LocalStorage ??= new Dictionary<string, string>(StringComparer.Ordinal);
        session.Cookies.RemoveAll(x => string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Domain));
    }
}
