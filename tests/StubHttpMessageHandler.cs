namespace ReShadeCentralized.Tests;

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(response(request));
}
