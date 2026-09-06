using System.Net.Http;

namespace GeroImperium.Core.Tests.Http;

/// <summary>Test seam for GeroImperiumClient -- records every request and answers via a caller-supplied
/// responder, so client behavior (URLs, methods, bodies, error-body handling) can be verified without real
/// hardware.</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await responder(request);
    }
}
