using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestOverlay.Update;

public sealed record UpdateRelease(long Id, string Tag, string Version, long AssetId, string AssetName, Uri DownloadUrl, long Size, string Sha256);
public sealed record UpdateOffer(UpdateRelease Release, string Notes, string Language);

public sealed class GitHubUpdateClient : IDisposable
{
    public const string Repository = "Nichipe-JCM/Mabinogi-Overlay";
    public const string MainExecutable = "Mabinogi Overlay.exe";
    public const string HelperExecutable = "MabinogiOverlay.Updater.exe";
    public const long MaxPackageSize = 512L * 1024 * 1024;
    private readonly HttpClient _http;
    public GitHubUpdateClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MabinogiOverlay-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    public async Task<UpdateRelease> LatestAsync(CancellationToken token) => ParseRelease(await ReadTextAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/latest"), 1024 * 1024, token));
    public async Task<UpdateRelease> ReleaseAsync(string tag, CancellationToken token)
    {
        _ = UpdateVersion.Parse(tag);
        var release = ParseRelease(await ReadTextAsync(new Uri($"https://api.github.com/repos/{Repository}/releases/tags/{Uri.EscapeDataString(tag)}"), 1024 * 1024, token));
        if (release.Tag != tag) throw new InvalidDataException("Release tag changed.");
        return release;
    }
    public static UpdateRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Only published regular releases are supported.");
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Missing tag.");
        var version = UpdateVersion.Parse(tag);
        var name = $"MabinogiOverlay-{version.Text}-win-x64-portable.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("Exactly one Windows x64 package is required.");
        var asset = assets[0];
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (size <= 0 || size > MaxPackageSize || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Missing or invalid package size/hash.");
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var prefix = $"https://github.com/{Repository}/releases/download/";
        if (!url.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) || url.UserInfo.Length != 0 || !url.IsDefaultPort || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidDataException("Unexpected package origin.");
        return new UpdateRelease(root.GetProperty("id").GetInt64(), tag, version.Text, asset.GetProperty("id").GetInt64(), name, url, size, digest[7..].ToUpperInvariant());
    }
    public async Task<UpdateOffer> OfferAsync(UpdateRelease release, string language, CancellationToken token)
    {
        if (language is not ("ko-KR" or "en-US")) throw new ArgumentException("Unsupported language.");
        var notes = await ReadTextAsync(new Uri($"https://raw.githubusercontent.com/{Repository}/{Uri.EscapeDataString(release.Tag)}/docs/updates/{Uri.EscapeDataString(release.Version)}/{language}.md"), 64 * 1024, token);
        // Restricted Markdown: headings and bullet paragraphs are displayed as text, never HTML or scripts.
        var lines = notes.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        if (!lines.Any(l => l.StartsWith("- ") || l.StartsWith("* "))) throw new InvalidDataException("In-app notes must contain a change list.");
        return new UpdateOffer(release, string.Join(Environment.NewLine, lines), language);
    }
    public async Task<string[]> FilesAsync(UpdateRelease release, CancellationToken token)
    {
        var json = await ReadTextAsync(new Uri($"https://raw.githubusercontent.com/{Repository}/{Uri.EscapeDataString(release.Tag)}/docs/updates/{Uri.EscapeDataString(release.Version)}/files.json"), 256 * 1024, token);
        var files = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException("Missing product file list.");
        PackageFiles.Validate(files);
        return files;
    }
    private async Task<string> ReadTextAsync(Uri url, int limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray()).TrimStart('\uFEFF');
    }
    public async Task DownloadAsync(UpdateRelease release, string path, IProgress<double>? progress, CancellationToken token)
    {
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        totalTimeout.CancelAfter(TimeSpan.FromMinutes(15));
        token = totalTimeout.Token;
        using var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Download size changed.");
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; long received = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                received += read;
                if (received > release.Size) throw new InvalidDataException("Download exceeds declared size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                progress?.Report(received * 100.0 / release.Size);
            }
            if (received != release.Size || Convert.ToHexString(hash.GetHashAndReset()) != release.Sha256) throw new InvalidDataException("Package SHA-256 mismatch.");
            await output.FlushAsync(token);
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
    public void Dispose() => _http.Dispose();
}
