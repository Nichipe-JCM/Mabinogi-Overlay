using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using TestOverlay.Update;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class GitHubUpdateClientTests
{
    private static string Json(bool prerelease = false, string? digest = null) => JsonSerializer.Serialize(new
    {
        id = 1, tag_name = "0.0.7-beta", draft = false, prerelease,
        assets = new[] { new { id = 2, name = "MabinogiOverlay-0.0.7-beta-win-x64-portable.zip", size = 3,
            digest = digest ?? "sha256:" + new string('a', 64), browser_download_url = "https://github.com/Nichipe-JCM/Mabinogi-Overlay/releases/download/0.0.7-beta/MabinogiOverlay-0.0.7-beta-win-x64-portable.zip" } }
    });
    [Fact]
    public void BetaNameIsAllowedOnlyWhenPublishedAsRegularRelease()
    {
        Assert.Equal("0.0.7-beta", GitHubUpdateClient.ParseRelease(Json()).Version);
        Assert.Throws<InvalidDataException>(() => GitHubUpdateClient.ParseRelease(Json(true)));
        Assert.Throws<InvalidDataException>(() => GitHubUpdateClient.ParseRelease(Json(digest: "")));
    }
    [Fact]
    public async Task NotesComeFromExactTagAndLanguageNotReleaseBody()
    {
        using var client = new GitHubUpdateClient(new Handler(request =>
        {
            Assert.Equal("https://raw.githubusercontent.com/Nichipe-JCM/Mabinogi-Overlay/0.0.7-beta/docs/updates/0.0.7-beta/ko-KR.md", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# 변경사항\n\n- 업데이트 기능 추가") };
        }));
        var offer = await client.OfferAsync(GitHubUpdateClient.ParseRelease(Json()), "ko-KR", TestContext.Current.CancellationToken);
        Assert.Equal("- 업데이트 기능 추가", offer.Notes);
    }
    [Fact]
    public async Task MissingLocalizedNotesFailsWithoutEnglishFallback()
    {
        using var client = new GitHubUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.OfferAsync(GitHubUpdateClient.ParseRelease(Json()), "ko-KR", TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task MissingNotesLeaveCoordinatorGrayAndWithoutInstallOffer()
    {
        using var log = new AppLog(enabled: false);
        using var coordinator = new UpdateCoordinator(log, new GitHubUpdateClient(new Handler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json().Replace("0.0.7-beta", "99.0.0")) }
                : new HttpResponseMessage(HttpStatusCode.NotFound))));
        await coordinator.CheckAsync();
        Assert.Equal(UpdateStatus.Failed, coordinator.Status);
        Assert.Null(coordinator.Offer);
    }
    [Fact]
    public async Task RateLimitIsRespectedWithoutAnotherNetworkRequest()
    {
        var calls = 0;
        using var client = new GitHubUpdateClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return response;
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.LatestAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.LatestAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task TamperedDownloadIsRemovedBeforeItCanBeInstalled()
    {
        using var client = new GitHubUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        var path = Path.Combine(Path.GetTempPath(), "overlay-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(GitHubUpdateClient.ParseRelease(Json()), path, null, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public async Task InstalledVersionCanResolveAVPrefixedTag()
    {
        var paths = new List<string>();
        using var client = new GitHubUpdateClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return paths.Count == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json().Replace("\"tag_name\":\"0.0.7-beta\"", "\"tag_name\":\"v0.0.7-beta\"")) };
        }));
        var release = await client.InstalledReleaseAsync("0.0.7-beta", TestContext.Current.CancellationToken);
        Assert.Equal("v0.0.7-beta", release.Tag);
        Assert.EndsWith("/tags/v0.0.7-beta", paths[1]);
    }
    [Fact]
    public async Task ConditionalRequestReusesTheMatchingCachedResponse()
    {
        var calls = 0;
        using var client = new GitHubUpdateClient(new Handler(request =>
        {
            if (++calls == 2)
            {
                Assert.Equal("\"release-one\"", request.Headers.IfNoneMatch.Single().ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json()) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-one\"");
            return response;
        }));
        Assert.Equal(await client.LatestAsync(TestContext.Current.CancellationToken), await client.LatestAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
