using System.Diagnostics;

namespace ReShadeCentralized;

public sealed class Downloads : IDisposable
{
    private const long MaximumTextBytes = 16L * 1024 * 1024;
    private readonly HttpClient client;

    public Downloads()
    {
        client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ReShadeCentralized (+https://github.com/AgentGTS/ReShade-Centralized)");
    }

    private async Task<HttpResponseMessage> ResponseAsync(string url, CancellationToken cancellationToken)
    {
        Uri uri = Url(url);
        HttpResponseMessage response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
            && uri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
        {
            response.Dispose();
            response = await client.GetAsync(
                new UriBuilder(uri) { Host = "www.reshade.me" }.Uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }

        return response;
    }

    public async Task<string> TextAsync(string url, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Timeout);
        using HttpResponseMessage response = await ResponseAsync(url, deadline.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaximumTextBytes, deadline.Token);
        return await response.Content.ReadAsStringAsync(deadline.Token);
    }

    public static Uri Url(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Only absolute HTTPS download URLs are supported.");
        }

        return uri;
    }

    public async Task FileAsync(
        string url,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        long maximumBytes = 8L * 1024 * 1024 * 1024)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Timeout);
        using HttpResponseMessage response = await ResponseAsync(url, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("Download exceeds the permitted size.");
        }
        await using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token);
        LocalFiles.CheckParents(destination);
        await using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long received = 0;
        var clock = Stopwatch.StartNew();

        while (true)
        {
            int count = await source.ReadAsync(buffer, deadline.Token);
            if (count == 0)
            {
                break;
            }

            received += count;
            if (received > maximumBytes)
            {
                throw new InvalidDataException("Download exceeds the permitted size.");
            }

            await target.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            if (clock.ElapsedMilliseconds >= 250)
            {
                progress?.Report(
                    $"Downloading {Path.GetFileName(new Uri(url).LocalPath)}: {received / 1024 / 1024} MB");
                clock.Restart();
            }
        }

        progress?.Report($"Downloaded {Path.GetFileName(new Uri(url).LocalPath)} ({received / 1024 / 1024} MB)");
    }

    public void Dispose() => client.Dispose();
}
