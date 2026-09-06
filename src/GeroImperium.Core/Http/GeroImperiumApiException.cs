using System.Net;

namespace GeroImperium.Core.Http;

/// <summary>Thrown for any non-2xx REST response. Body is plain text, not JSON (doc/windows_app_api_guide.md's
/// Gotchas -- e.g. "404" + "No such row") -- never try to parse it as a row.</summary>
public sealed class GeroImperiumApiException(HttpStatusCode statusCode, string responseBody)
    : Exception($"GeroImperium device returned {(int)statusCode} {statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
