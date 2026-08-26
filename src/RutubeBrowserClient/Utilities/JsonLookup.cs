using System.Globalization;
using System.Text.Json;

namespace RutubeBrowserClient;

internal static class JsonLookup
{
    public static JsonElement Unwrap(JsonElement root)
    {
        foreach (var name in new[] { "data", "result", "response", "video", "stream", "profile" })
            if (root.ValueKind == JsonValueKind.Object && TryProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        return root;
    }

    public static string? String(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (Find(root, name) is { } value)
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return value.ToString();
            }
        return null;
    }

    public static bool? Bool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (Find(root, name) is { } value)
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
            }
        return null;
    }

    public static long? Int64(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (Find(root, name) is { } value)
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
                if (value.ValueKind == JsonValueKind.String &&
                    long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
        return null;
    }

    public static DateTimeOffset? Date(JsonElement root, params string[] names)
    {
        var text = String(root, names);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    public static Uri? Uri(JsonElement root, params string[] names)
    {
        var text = String(root, names);
        return System.Uri.TryCreate(text, UriKind.Absolute, out var parsed) ? parsed : null;
    }

    public static JsonElement? Find(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            }
            foreach (var wrapper in new[] { "data", "result", "response", "profile", "video", "stream", "owner" })
                if (TryProperty(root, wrapper, out var nested))
                {
                    var found = Find(nested, name);
                    if (found is not null) return found;
                }
        }
        return null;
    }

    public static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        foreach (var name in new[] { "items", "results", "data", "categories", "videos", "streams" })
            if (root.ValueKind == JsonValueKind.Object && TryProperty(root, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var nested = Items(value).ToArray();
                    if (nested.Length > 0) return nested;
                }
            }
        return [];
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            { value = property.Value; return true; }
        value = default;
        return false;
    }
}
