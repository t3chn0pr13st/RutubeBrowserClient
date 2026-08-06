using System.Net;

namespace RutubeBrowserClient;

public class RutubeClientException : Exception
{
    public RutubeClientException(string message) : base(message) { }
    public RutubeClientException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class RutubeSessionExpiredException : RutubeClientException
{
    public RutubeSessionExpiredException(string message = "Rutube session is missing or expired. Interactive sign-in is required.") : base(message) { }
}

public sealed class RutubeContractUnavailableException : RutubeClientException
{
    public RutubeContractUnavailableException(string message) : base(message) { }
}

public class RutubeApiException : RutubeClientException
{
    public RutubeApiException(
        string operation,
        HttpStatusCode? statusCode,
        string? errorCode,
        string safeMessage,
        string? requestId = null,
        Exception? innerException = null)
        : base(BuildMessage(operation, statusCode, errorCode, safeMessage, requestId),
            new Exception(SafeText.Sanitize(innerException?.Message ?? safeMessage, 400)))
    {
        Operation = operation;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RequestId = requestId;
    }

    public string Operation { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? ErrorCode { get; }
    public string? RequestId { get; }
    public bool IsTransient => StatusCode is null || StatusCode == HttpStatusCode.RequestTimeout ||
        StatusCode == (HttpStatusCode)429 || (int?)StatusCode >= 500;

    private static string BuildMessage(string operation, HttpStatusCode? status, string? code, string message, string? requestId)
    {
        var details = status is null ? "transport failure" : $"HTTP {(int)status.Value}";
        if (!string.IsNullOrWhiteSpace(code)) details += $", code={SafeText.Sanitize(code, 80)}";
        if (!string.IsNullOrWhiteSpace(requestId)) details += $", request={SafeText.Sanitize(requestId, 80)}";
        return $"Rutube operation '{SafeText.Sanitize(operation, 80)}' failed ({details}): {SafeText.Sanitize(message, 400)}";
    }
}

public sealed class RutubeOutcomeUnknownException : RutubeApiException
{
    public RutubeOutcomeUnknownException(string operation, string clientReference, Exception innerException)
        : base(operation, null, "outcome_unknown",
            $"The request outcome is unknown. Reconcile by owner and client reference '{SafeText.Sanitize(clientReference, 80)}' before retrying.",
            innerException: innerException)
    {
        ClientReference = clientReference;
    }

    public string ClientReference { get; }
}
