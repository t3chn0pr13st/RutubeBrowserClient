using System.Text;

namespace RutubeBrowserClient;

/// <summary>Atomic JSON session store; files are created as 0600 and directories as 0700 on Unix.</summary>
public sealed class FileRutubeSessionStore : IRutubeSessionStore
{
    private readonly string _path;
    private readonly Action<string>? _onWarning;

    public FileRutubeSessionStore(string path, Action<string>? onWarning = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Session file path is required.", nameof(path))
            : Path.GetFullPath(path);
        _onWarning = onWarning;
    }

    public async Task<RutubeSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(_path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return RutubeSessionSerializer.Deserialize(json);
        }
        catch (RutubeClientException) { return null; }
        catch (IOException ex)
        {
            throw new RutubeClientException($"Could not read the Rutube session file '{Path.GetFileName(_path)}'.", ex);
        }
    }

    public async Task SaveAsync(RutubeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            RestrictDirectory(directory);
        }

        var temporary = _path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                await writer.WriteAsync(RutubeSessionSerializer.Serialize(session).AsMemory(), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            RestrictFile(_path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { _onWarning?.Invoke($"Could not restrict session directory permissions: {ex.GetType().Name}."); }
    }

    private void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { _onWarning?.Invoke($"Could not restrict session file permissions: {ex.GetType().Name}."); }
    }
}
