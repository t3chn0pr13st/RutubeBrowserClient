namespace RutubeBrowserClient;

/// <summary>Re-openable stream source. The factory can be invoked again after a retry or restart boundary.</summary>
public sealed class RutubeUploadSource
{
    private RutubeUploadSource(string fileName, string contentType, long length, Func<CancellationToken, ValueTask<Stream>> open)
    {
        FileName = fileName;
        ContentType = contentType;
        Length = length;
        OpenReadAsync = open;
    }

    public string FileName { get; }
    public string ContentType { get; }
    public long Length { get; }
    public Func<CancellationToken, ValueTask<Stream>> OpenReadAsync { get; }

    public static RutubeUploadSource FromFile(string path, string? contentType = null)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Upload source was not found.", fullPath);
        return Create(info.Name, contentType ?? MimeFromExtension(info.Extension), info.Length,
            _ => ValueTask.FromResult<Stream>(File.OpenRead(fullPath)));
    }

    public static RutubeUploadSource Create(
        string fileName,
        string contentType,
        long length,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("File name is required.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(contentType)) throw new ArgumentException("Content type is required.", nameof(contentType));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        ArgumentNullException.ThrowIfNull(openReadAsync);
        return new RutubeUploadSource(Path.GetFileName(fileName), contentType, length, openReadAsync);
    }

    private static string MimeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream"
    };
}
