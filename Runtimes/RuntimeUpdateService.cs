using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ReShadeCentralized;

public sealed class RuntimeUpdateService : IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly Uri TagsEndpoint = new("https://api.github.com/repos/crosire/reshade/tags?per_page=100");
    private readonly HttpClient client;
    private EntityTagHeaderValue? cachedTag;
    private Version? cachedVersion;

    public DateTimeOffset NextAllowedCheck { get; private set; } = DateTimeOffset.MinValue;

    public RuntimeUpdateService()
    {
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ReShadeCentralized (+https://github.com/AgentGTS/ReShade-Centralized)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<Version> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        deadline.Token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow < NextAllowedCheck)
        {
            throw new HttpRequestException("GitHub's update-check rate limit has not yet reset.", null,
                HttpStatusCode.TooManyRequests);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, TagsEndpoint);
        if (cachedVersion != null && cachedTag != null)
        {
            request.Headers.IfNoneMatch.Add(cachedTag);
        }

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is Uri actual && !IsTagsEndpoint(actual))
        {
            throw new InvalidDataException("The ReShade update check reached an untrusted address.");
        }

        bool rateLimited = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
        if (rateLimited || response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(value => value.Trim() == "0"))
        {
            NextAllowedCheck = RetryAfter(response);
        }
        else
        {
            NextAllowedCheck = DateTimeOffset.MinValue;
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cachedVersion == null || cachedTag == null)
            {
                throw new InvalidDataException("GitHub returned an uncached update-check response.");
            }

            deadline.Token.ThrowIfCancellationRequested();
            return cachedVersion;
        }

        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException("The ReShade version response is incomplete.");
        }

        if (response.Content.Headers.ContentLength is long length && length > MaximumResponseBytes)
        {
            throw new InvalidDataException("The ReShade version response is too large.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("The ReShade version response is too large.");
            }

            content.Write(buffer, 0, count);
        }

        Version latest = ParseTags(content.GetBuffer().AsMemory(0, checked((int)content.Length)));
        deadline.Token.ThrowIfCancellationRequested();
        cachedVersion = latest;
        cachedTag = response.Headers.ETag;
        return latest;
    }

    private static bool IsTagsEndpoint(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(TagsEndpoint.Host, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.Equals(TagsEndpoint.AbsolutePath, StringComparison.Ordinal)
        && uri.Query.Equals(TagsEndpoint.Query, StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Fragment);

    private static Version ParseTags(ReadOnlyMemory<byte> content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub returned invalid ReShade version metadata.");
        }

        Version? latest = null;
        foreach (JsonElement tag in document.RootElement.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.Object || !tag.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("GitHub returned an invalid ReShade version tag.");
            }

            string text = name.GetString()!;
            if (text.Length > 64)
            {
                continue;
            }
            string number = text.StartsWith('v') ? text[1..] : text;
            string[] components = number.Split('.');
            if (components.Length != 3 || components.Any(component => component.Length == 0
                    || component.Any(character => character is < '0' or > '9')))
            {
                continue;
            }

            if (!Version.TryParse(number, out Version? candidate))
            {
                throw new InvalidDataException("GitHub returned an invalid ReShade version number.");
            }

            if (latest == null || candidate > latest)
            {
                latest = candidate;
            }
        }

        return latest ?? throw new InvalidDataException("GitHub returned no stable ReShade version tags.");
    }

    private static DateTimeOffset RetryAfter(HttpResponseMessage response)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? next = null;
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
        if (retry?.Date is DateTimeOffset date && date > now)
        {
            next = date;
        }
        if (retry?.Delta is TimeSpan delay && delay > TimeSpan.Zero)
        {
            DateTimeOffset requested = delay > DateTimeOffset.MaxValue - now
                ? DateTimeOffset.MaxValue : now.Add(delay);
            if (next == null || requested > next)
            {
                next = requested;
            }
        }
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            foreach (string value in values)
            {
                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                    && seconds >= 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                {
                    DateTimeOffset reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (reset > now && (next == null || reset > next))
                    {
                        next = reset;
                    }
                }
            }
        }

        return next ?? now.AddHours(1);
    }

    public void Dispose() => client.Dispose();
}
