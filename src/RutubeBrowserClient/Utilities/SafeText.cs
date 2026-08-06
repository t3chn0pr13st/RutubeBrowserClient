using System.Text.RegularExpressions;

namespace RutubeBrowserClient;

internal static partial class SafeText
{
    [GeneratedRegex("""(?i)(access[_-]?token|refresh[_-]?token|csrf|session|stream[_-]?key|authorization|cookie)(["']?\s*[:=]\s*["']?|%3[dD])([^&\s,;"']+)""")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex("(?i)bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerRegex();

    [GeneratedRegex("(?i)([?&](?:token|key|signature|secret|auth)=[^&#\\s]+)")]
    private static partial Regex QuerySecretRegex();

    public static string Sanitize(string? value, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(value)) return "No details returned.";
        var result = value.Replace('\r', ' ').Replace('\n', ' ');
        result = NamedSecretRegex().Replace(result, "$1$2[REDACTED]");
        result = BearerRegex().Replace(result, "Bearer [REDACTED]");
        result = QuerySecretRegex().Replace(result, "?[REDACTED]");
        if (result.Length > maxLength) result = result[..maxLength] + "…";
        return result;
    }
}
