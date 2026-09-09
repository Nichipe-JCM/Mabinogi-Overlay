using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using TestOverlay.Update;
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
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
