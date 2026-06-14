using System.Net;
using System.Net.Http;

namespace Poe2LootLens.Tests;

internal sealed class FakeHttpMessageHandler(string response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response),
            RequestMessage = request,
        });
}

// PriceRepository fetches its exchange categories concurrently. Keep captured request metadata
// synchronized so the tests remain deterministic under parallel SendAsync calls.
internal sealed class CapturingFakeHttpHandler(string response) : HttpMessageHandler
{
    private readonly object _lock = new();
    public List<string> Urls { get; } = [];
    public List<string?> Referers { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            Referers.Add(
                request.Headers.TryGetValues("Referer", out var values)
                    ? string.Join(string.Empty, values)
                    : null);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response),
            RequestMessage = request,
        });
    }
}

internal sealed class CountingFakeHttpHandler(byte[] response) : HttpMessageHandler
{
    private int _requestCount;
    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(response),
            RequestMessage = request,
        });
    }
}

internal sealed class FailingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request,
        });
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        System.IO.Path.GetRandomFileName());

    public TempDir() => System.IO.Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Path, true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
