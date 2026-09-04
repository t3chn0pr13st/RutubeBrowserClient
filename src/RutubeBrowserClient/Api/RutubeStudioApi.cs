using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RutubeBrowserClient;

internal sealed partial class RutubeStudioApi : IDisposable
{
    private readonly RutubeSession _session;
    private readonly RutubeClientOptions _options;
    private readonly Func<CancellationToken, Task> _persist;
    private readonly HttpClient _apiClient;
    private readonly HttpClient _uploadClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public RutubeStudioApi(RutubeSession session, RutubeClientOptions options, Func<CancellationToken, Task> persist)
    {
        _session = session;
        _options = options;
        _persist = persist;
        _apiClient = new HttpClient(options.ApiHttpMessageHandlerFactory?.Invoke() ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        _uploadClient = new HttpClient(options.UploadHttpMessageHandlerFactory?.Invoke() ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<JsonDocument> GetStudioAsync(string path, string operation, CancellationToken ct) =>
        SendAsync(_apiClient, () => CreateRequest(HttpMethod.Get, Studio(path)), operation, _options.RequestTimeout, false, null, ct);

    public Task<JsonDocument> GetPublicAsync(string path, string operation, CancellationToken ct) =>
        SendAsync(_apiClient, () => CreateRequest(HttpMethod.Get, Public(path)), operation, _options.RequestTimeout, false, null, ct);

    public Task<JsonDocument> PostStudioAsync(string path, object payload, string operation, CancellationToken ct,
        bool outcomeUnknownOnTransportFailure = false, string? clientReference = null, string? idempotencyKey = null)
    {
        EnsurePrivateApiEnabled();
        return SendAsync(_apiClient, () => CreateJsonRequest(HttpMethod.Post, Studio(path), payload, idempotencyKey), operation,
            _options.RequestTimeout, outcomeUnknownOnTransportFailure, clientReference, ct);
    }

    public Task<JsonDocument> PatchStudioAsync(string path, object payload, string operation, CancellationToken ct) =>
        SendAsync(_apiClient, () => CreateJsonRequest(HttpMethod.Patch, Studio(path), payload), operation,
            _options.RequestTimeout, false, null, ct);

    public Task<JsonDocument> PatchPublicAsync(string path, object payload, string operation, CancellationToken ct) =>
        SendAsync(_apiClient, () => CreateJsonRequest(HttpMethod.Patch, Public(path), payload), operation, _options.RequestTimeout, false, null, ct);

    public Task<JsonDocument> DeletePublicAsync(string path, string operation, CancellationToken ct) =>
        SendAsync(_apiClient, () => CreateRequest(HttpMethod.Delete, Public(path)), operation, _options.RequestTimeout, false, null, ct);

    public async Task<JsonDocument> PostMultipartAsync(
        Uri uri,
        IReadOnlyDictionary<string, string?> fields,
        string fieldName,
        RutubeUploadSource source,
        string operation,
        CancellationToken ct,
        bool outcomeUnknownOnTransportFailure = false,
        string? clientReference = null,
        string? idempotencyKey = null)
    {
        return await SendAsync(_uploadClient, () =>
        {
            var request = CreateRequest(HttpMethod.Post, uri);
            if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            request.Content = new DeferredMultipartContent(fields, fieldName, source, ct);
            return request;
        }, operation, _options.UploadTimeout, outcomeUnknownOnTransportFailure, clientReference, ct).ConfigureAwait(false);
    }

    public Uri Studio(string path) => new(_options.StudioApiUri, path.TrimStart('/'));
    public Uri Public(string path) => new(_options.PublicApiUri, path.TrimStart('/'));
    public Uri Upload(string path) => new(_options.UploadUri, path.TrimStart('/'));

    public async Task<Uri> CreateTusUploadAsync(
        Uri endpoint,
        long length,
        IReadOnlyDictionary<string, string> metadata,
        string clientReference,
        CancellationToken cancellationToken)
    {
        using var request = CreateTusRequest(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("Upload-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Upload-Metadata", EncodeTusMetadata(metadata));
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        using var response = await SendTusAsync(request, "video.upload.tus-create", cancellationToken,
            outcomeUnknownOnTransportFailure: true, clientReference).ConfigureAwait(false);
        var location = response.Headers.Location;
        if (location is null)
            throw new RutubeApiException("video.upload.tus-create", response.StatusCode, "missing_location",
                "Rutube TUS response did not contain a Location header.", RequestId(response));
        var resolved = location.IsAbsoluteUri ? location : new Uri(endpoint, location);
        ValidateTusUri(resolved);
        return resolved;
    }

    public async Task<long> GetTusOffsetAsync(Uri uploadUrl, CancellationToken cancellationToken)
    {
        using var request = CreateTusRequest(HttpMethod.Head, uploadUrl);
        using var response = await SendTusAsync(request, "video.upload.tus-head", cancellationToken).ConfigureAwait(false);
        return ParseTusOffset(response, "video.upload.tus-head");
    }

    public async Task<long> PatchTusAsync(
        Uri uploadUrl,
        RutubeUploadSource source,
        long offset,
        long count,
        CancellationToken cancellationToken)
    {
        using var request = CreateTusRequest(HttpMethod.Patch, uploadUrl);
        request.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new DeferredRangeContent(source, offset, count, cancellationToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        using var response = await SendTusAsync(request, "video.upload.tus-patch", cancellationToken).ConfigureAwait(false);
        var remoteOffset = ParseTusOffset(response, "video.upload.tus-patch");
        if (remoteOffset <= offset || remoteOffset > source.Length)
            throw new RutubeApiException("video.upload.tus-patch", response.StatusCode, "invalid_offset",
                "Rutube TUS response returned an invalid upload offset.", RequestId(response));
        return remoteOffset;
    }

    private async Task<JsonDocument> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        string operation,
        TimeSpan timeout,
        bool outcomeUnknownOnTransportFailure,
        string? clientReference,
        CancellationToken cancellationToken)
    {
        if (_session.AccessTokenExpiresAt is { } expiresAt
            && expiresAt <= _options.TimeProvider.GetUtcNow() + _options.TokenExpirySkew)
            await TryRefreshAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = requestFactory();
            ApplySession(request);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                if (outcomeUnknownOnTransportFailure && !string.IsNullOrWhiteSpace(clientReference))
                    throw new RutubeOutcomeUnknownException(operation, clientReference, ex);
                throw new RutubeApiException(operation, null, "transport", "The provider did not return a definitive response.", innerException: ex);
            }

            using (response)
            {
                CaptureSetCookies(response);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && await TryRefreshAsync(cancellationToken).ConfigureAwait(false))
                    continue;

                var body = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw CreateApiException(operation, response, body);

                await _persist(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                try { return JsonDocument.Parse(body); }
                catch (JsonException ex)
                {
                    throw new RutubeApiException(operation, response.StatusCode, "invalid_json",
                        "Rutube returned a successful response with an invalid JSON body.", RequestId(response), ex);
                }
            }
        }
        throw new RutubeSessionExpiredException();
    }

    private async Task<HttpResponseMessage> SendTusAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken,
        bool outcomeUnknownOnTransportFailure = false,
        string? clientReference = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.UploadTimeout);
        try
        {
            var response = await _uploadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            var body = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var exception = CreateApiException(operation, response, body);
            response.Dispose();
            throw exception;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            if (outcomeUnknownOnTransportFailure && !string.IsNullOrWhiteSpace(clientReference))
                throw new RutubeOutcomeUnknownException(operation, clientReference, ex);
            throw new RutubeApiException(operation, null, "transport", "The provider did not return a definitive response.", innerException: ex);
        }
    }

    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_session.RefreshToken)) return false;
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = CreateJsonRequest(HttpMethod.Post, Studio(_options.RefreshTokenPath), new { refresh = _session.RefreshToken });
            ApplyCookiesAndHeaders(request, includeAuthorization: false);
            using var response = await _apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var access = JsonLookup.String(doc.RootElement, "access", "access_token", "token");
            if (string.IsNullOrWhiteSpace(access)) return false;
            _session.AccessToken = access;
            _session.RefreshToken = JsonLookup.String(doc.RootElement, "refresh", "refresh_token") ?? _session.RefreshToken;
            var expires = JsonLookup.String(doc.RootElement, "expires_at", "access_expires_at");
            if (long.TryParse(expires, out var unix)) _session.AccessTokenExpiresAtUnix = unix;
            else if (long.TryParse(JsonLookup.String(doc.RootElement, "expires_in"), out var seconds))
                _session.AccessTokenExpiresAtUnix = _options.TimeProvider.GetUtcNow().AddSeconds(seconds).ToUnixTimeSeconds();
            CaptureSetCookies(response);
            await _persist(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return false; }
        finally { _refreshLock.Release(); }
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, object payload, string? idempotencyKey = null)
    {
        var request = CreateRequest(method, uri);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, RutubeSessionSerializer.JsonOptions), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            request.Headers.TryAddWithoutValidation("X-Request-ID", idempotencyKey);
        }
        return request;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private HttpRequestMessage CreateTusRequest(HttpMethod method, Uri uri)
    {
        ValidateTusUri(uri);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Tus-Resumable", "1.0.0");
        request.Headers.UserAgent.ParseAdd(_session.UserAgent ?? _options.UserAgent);
        request.Headers.TryAddWithoutValidation("Origin", new Uri(_options.StudioBaseUrl).GetLeftPart(UriPartial.Authority));
        request.Headers.Referrer = new Uri(_options.StudioBaseUrl);
        return request;
    }

    private void ValidateTusUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IdnHost.Equals(_options.UploadUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo))
            throw new RutubeContractUnavailableException("Rutube TUS URL must use the configured HTTPS upload host.");
    }

    private void ApplySession(HttpRequestMessage request) => ApplyCookiesAndHeaders(request, includeAuthorization: true);

    private void ApplyCookiesAndHeaders(HttpRequestMessage request, bool includeAuthorization)
    {
        request.Headers.UserAgent.ParseAdd(_session.UserAgent ?? _options.UserAgent);
        var cookies = string.Join("; ", _session.Cookies.Where(x => !x.IsExpired).Select(x => $"{x.Name}={x.Value}"));
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (includeAuthorization && !string.IsNullOrWhiteSpace(_session.AccessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            var csrf = _session.CsrfToken ?? _session.Cookies.FirstOrDefault(x => x.Name.Contains("csrf", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(csrf)) request.Headers.TryAddWithoutValidation("X-CSRFToken", csrf);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.Referrer = new Uri(_options.StudioBaseUrl);
            request.Headers.TryAddWithoutValidation("Origin", new Uri(_options.StudioBaseUrl).GetLeftPart(UriPartial.Authority));
        }
    }

    private void CaptureSetCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var header in values)
        {
            var first = header.Split(';', 2)[0];
            var equals = first.IndexOf('=');
            if (equals <= 0) continue;
            var name = first[..equals].Trim();
            var value = first[(equals + 1)..].Trim();
            var existing = _session.Cookies.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                _session.Cookies.Add(new RutubeCookie { Name = name, Value = value, Domain = ".rutube.ru", Path = "/", Secure = true });
            else existing.Value = value;
            if (name.Contains("csrf", StringComparison.OrdinalIgnoreCase)) _session.CsrfToken = value;
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var buffer = new char[8192];
        var builder = new StringBuilder();
        while (builder.Length < 4 * 1024 * 1024)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 4 * 1024 * 1024 - builder.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static RutubeApiException CreateApiException(string operation, HttpResponseMessage response, string body)
    {
        string? code = null;
        var message = response.ReasonPhrase ?? "Provider rejected the request.";
        try
        {
            using var doc = JsonDocument.Parse(body);
            code = JsonLookup.String(doc.RootElement, "code", "error_code", "slug");
            message = JsonLookup.String(doc.RootElement, "message", "detail", "error", "non_field_errors") ?? message;
        }
        catch (JsonException) { }
        return response.StatusCode == HttpStatusCode.Unauthorized
            ? new RutubeApiException(operation, response.StatusCode, code ?? "session_expired", "Rutube session is expired.", RequestId(response))
            : new RutubeApiException(operation, response.StatusCode, code, message, RequestId(response));
    }

    private static long ParseTusOffset(HttpResponseMessage response, string operation)
    {
        if (response.Headers.TryGetValues("Upload-Offset", out var values) &&
            long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset >= 0)
            return offset;
        throw new RutubeApiException(operation, response.StatusCode, "missing_offset",
            "Rutube TUS response did not contain a valid upload offset.", RequestId(response));
    }

    private static string EncodeTusMetadata(IReadOnlyDictionary<string, string> metadata) =>
        string.Join(",", metadata.Where(x => !string.IsNullOrWhiteSpace(x.Key)).Select(x =>
            $"{TusMetadataKey(x.Key)} {Convert.ToBase64String(Encoding.UTF8.GetBytes(x.Value ?? string.Empty))}"));

    private static string TusMetadataKey(string value)
    {
        if (value.Length is < 1 or > 64 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("TUS metadata key is invalid.", nameof(value));
        return value;
    }

    private static string? RequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "X-Request-ID", "X-Correlation-ID", "Trace-ID" })
            if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return null;
    }

    internal void EnsurePrivateApiEnabled()
    {
        if (!_options.EnablePrivateStudioApi)
            throw new RutubeContractUnavailableException(
                $"Rutube Studio private contract '{_options.ContractVersion}' is disabled. Set EnablePrivateStudioApi only after a capability probe/canary.");
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    public void Dispose()
    {
        _apiClient.Dispose();
        _uploadClient.Dispose();
        _refreshLock.Dispose();
    }

    private sealed class DeferredMultipartContent : HttpContent
    {
        private readonly IReadOnlyDictionary<string, string?> _fields;
        private readonly string _fieldName;
        private readonly RutubeUploadSource _source;
        private readonly CancellationToken _outerToken;
        private readonly string _boundary = "----------------" + Guid.NewGuid().ToString("N");

        public DeferredMultipartContent(IReadOnlyDictionary<string, string?> fields, string fieldName, RutubeUploadSource source, CancellationToken outerToken)
        {
            _fields = fields;
            _fieldName = fieldName;
            _source = source;
            _outerToken = outerToken;
            Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={_boundary}");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
            foreach (var (name, value) in _fields.Where(x => x.Value is not null))
            {
                await WriteAsync(stream, $"--{_boundary}\r\nContent-Disposition: form-data; name=\"{SafeName(name)}\"\r\n\r\n{value}\r\n", linked.Token).ConfigureAwait(false);
            }
            await WriteAsync(stream, $"--{_boundary}\r\nContent-Disposition: form-data; name=\"{SafeName(_fieldName)}\"; filename=\"{SafeName(_source.FileName)}\"\r\nContent-Type: {_source.ContentType}\r\n\r\n", linked.Token).ConfigureAwait(false);
            await using (var source = await _source.OpenReadAsync(linked.Token).ConfigureAwait(false))
                await source.CopyToAsync(stream, linked.Token).ConfigureAwait(false);
            await WriteAsync(stream, $"\r\n--{_boundary}--\r\n", linked.Token).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        private static Task WriteAsync(Stream stream, string text, CancellationToken ct) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();
        private static string SafeName(string value) => HeaderUnsafeRegex().Replace(value, "_");
    }

    private sealed class DeferredRangeContent : HttpContent
    {
        private readonly RutubeUploadSource _source;
        private readonly long _offset;
        private readonly long _count;
        private readonly CancellationToken _outerToken;

        public DeferredRangeContent(RutubeUploadSource source, long offset, long count, CancellationToken outerToken)
        {
            if (offset < 0 || count <= 0 || offset + count > source.Length) throw new ArgumentOutOfRangeException(nameof(count));
            _source = source;
            _offset = offset;
            _count = count;
            _outerToken = outerToken;
            Headers.ContentLength = count;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_outerToken);
            await using var source = await _source.OpenReadAsync(linked.Token).ConfigureAwait(false);
            await SeekOrSkipAsync(source, _offset, linked.Token).ConfigureAwait(false);
            var remaining = _count;
            var buffer = new byte[128 * 1024];
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), linked.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Upload source ended before the declared range was read.");
                await stream.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                remaining -= read;
            }
        }

        protected override bool TryComputeLength(out long length) { length = _count; return true; }

        private static async Task SeekOrSkipAsync(Stream stream, long offset, CancellationToken cancellationToken)
        {
            if (offset == 0) return;
            if (stream.CanSeek)
            {
                if (stream.Seek(offset, SeekOrigin.Begin) != offset) throw new EndOfStreamException("Upload source cannot seek to the remote offset.");
                return;
            }
            var buffer = new byte[128 * 1024];
            var remaining = offset;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Upload source ended before the remote offset.");
                remaining -= read;
            }
        }
    }

    [GeneratedRegex("[\\r\\n\\\"]")]
    private static partial Regex HeaderUnsafeRegex();
}
