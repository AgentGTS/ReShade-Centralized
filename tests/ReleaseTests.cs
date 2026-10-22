using System.Net;
using System.Text.Json;

namespace ReShadeCentralized.Tests;

internal static partial class TestSuite
{
    private static Task ReleaseVersionsFollowSemanticPrecedenceAsync()
    {
        string[] ordered =
        [
            "0.6.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.1", "1.0.0-beta.2", "1.0.0-beta.10", "1.0.0-rc.1", "1.0.0",
            "1.0.1", "1.1.0", "1.10.0", "2.0.0", "999999999999999999999.0.0"
        ];
        for (var index = 1; index < ordered.Length; index++)
        {
            ReleaseVersion older = ReleaseVersion.Parse(ordered[index - 1]);
            ReleaseVersion newer = ReleaseVersion.Parse(ordered[index]);
            AssertTrue(older.CompareTo(newer) < 0 && newer.CompareTo(older) > 0,
                $"Incorrect precedence: {older.Text}, {newer.Text}");
        }

        AssertTrue(ReleaseVersion.Parse("v1.0.0-beta.1+build.123")
            .CompareTo(ReleaseVersion.Parse("1.0.0-beta.1+other")) == 0);
        AssertTrue(ReleaseVersion.Parse("1.0.0+build").CompareTo(ReleaseVersion.Parse("1.0.0")) == 0);
        AssertTrue(ReleaseVersion.Parse("1.0.0-beta.1").IsPrerelease);
        AssertTrue(!ReleaseVersion.Parse("1.0.0").IsPrerelease);
        string[] invalid =
        [
            "", "1.0", "1.0.0.0", "01.0.0", "1.00.0", "1.0.01", "1.0.0-beta.01",
            "1.0.0-", "1.0.0+", "1.0.0-beta..1", "1.0.0\n", "release-1.0.0", "1.0.0-β"
        ];
        foreach (string text in invalid)
        {
            AssertTrue(!ReleaseVersion.TryParse(text, out _), $"Invalid version accepted: {text}");
        }

        AssertTrue(!ReleaseVersion.TryParse(null, out _));
        return Task.CompletedTask;
    }

    private static async Task ReleaseCheckRespectsChannelsAndVersionOrderAsync()
    {
        var releases = new[]
        {
            new { tag_name = "v1.0.0-beta.10", draft = false, prerelease = true },
            new { tag_name = "v1.0.0-beta.2", draft = false, prerelease = true },
            new { tag_name = "v0.6.0", draft = false, prerelease = false },
            new { tag_name = "v9.0.0", draft = true, prerelease = false },
            new { tag_name = "not-a-version", draft = false, prerelease = false },
            new { tag_name = "v2.0.0-beta.1", draft = false, prerelease = false },
            new { tag_name = "v3.0.0", draft = false, prerelease = true }
        };
        string payload = JsonSerializer.Serialize(releases);
        string? requested = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        });
        using var manager = new Manager(
            new StateStore(CreateTestDirectory("release-channels")),
            new InMemoryLayerRegistry(),
            new Downloads(handler));

        AssertTrue(await manager.CheckReleaseAsync(None) == "v0.6.0");
        AssertTrue(requested ==
            "https://api.github.com/repos/AgentGTS/ReShade-Centralized/releases?per_page=100&page=1");
        AssertTrue(await manager.CheckReleaseAsync(includePrereleases: true, None) == "v3.0.0");

        payload = JsonSerializer.Serialize(releases.Take(2));
        AssertTrue(await manager.CheckReleaseAsync(None) == null);
        AssertTrue(await manager.CheckReleaseAsync(includePrereleases: true, None) == "v1.0.0-beta.10");

        payload = JsonSerializer.Serialize(releases.Take(2).Append(
            new { tag_name = "v1.0.0", draft = false, prerelease = false }));
        AssertTrue(await manager.CheckReleaseAsync(includePrereleases: true, None) == "v1.0.0");
        payload = "[]";
        AssertTrue(await manager.CheckReleaseAsync(includePrereleases: true, None) == null);
    }

    private static async Task ReleaseCheckPaginatesAndPreservesFailuresAsync()
    {
        int requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            string tag = requestCount == 1 ? "v1.0.0-beta.1" : "v1.0.0";
            string payload = JsonSerializer.Serialize(Enumerable.Repeat(
                new { tag_name = tag, draft = false, prerelease = requestCount == 1 },
                requestCount == 1 ? 100 : 1));
            AssertTrue(request.RequestUri!.Query.EndsWith($"page={requestCount}"));
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        });
        using var manager = new Manager(
            new StateStore(CreateTestDirectory("release-pages")),
            new InMemoryLayerRegistry(),
            new Downloads(handler));
        AssertTrue(await manager.CheckReleaseAsync(None) == "v1.0.0" && requestCount == 2);

        using var failingManager = new Manager(
            new StateStore(CreateTestDirectory("release-failure")),
            new InMemoryLayerRegistry(),
            new Downloads(new StubHttpMessageHandler(_ => new(HttpStatusCode.Forbidden))));
        try
        {
            await failingManager.CheckReleaseAsync(None);
            throw new Exception("An HTTP failure was reported as an absent release.");
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden)
        {
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await manager.CheckReleaseAsync(cancellation.Token);
            throw new Exception("A canceled release check completed successfully.");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
