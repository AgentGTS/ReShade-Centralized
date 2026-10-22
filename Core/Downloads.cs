using System.Diagnostics;

namespace ReShadeCentralized;

public sealed class Downloads : IDisposable
{
    private readonly HttpClient client;

    public Downloads(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
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
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
            uri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
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
        using HttpResponseMessage response = await ResponseAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public static Uri Url(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Only absolute HTTPS download URLs are supported.");
        }

        return uri;
    }

    public async Task FileAsync(
        string url,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ResponseAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = File.Create(destination);
        byte[] buffer = new byte[81920];
        long received = 0;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            received += count;
            if (received > 8L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("Download exceeds the 8 GB limit.");
            }

            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
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
