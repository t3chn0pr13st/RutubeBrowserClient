namespace RutubeBrowserClient;

internal static class RequestValidation
{
    public static void Metadata(string title, string description, string categoryId)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title is required.", nameof(title));
        if (title.Length > 100) throw new ArgumentOutOfRangeException(nameof(title), "Title must not exceed 100 characters.");
        if (description?.Length > 5000) throw new ArgumentOutOfRangeException(nameof(description), "Description must not exceed 5000 characters.");
        if (string.IsNullOrWhiteSpace(categoryId)) throw new ArgumentException("Category is required.", nameof(categoryId));
    }

    public static void ClientReference(string? value, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Client reference is required.", nameof(value));
        if (value?.Length > 100) throw new ArgumentOutOfRangeException(nameof(value));
        if (value is not null && value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("Client reference may contain letters, digits, '.', '-' and '_' only.", nameof(value));
    }
}
