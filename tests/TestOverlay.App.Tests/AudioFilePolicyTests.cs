using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class AudioFilePolicyTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mabinogi-overlay-audio-policy-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".wav")]
    [InlineData(".wma")]
    [InlineData(".m4a")]
    public void Validate_AcceptsSupportedFileWithMatchingHeader(string extension)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"sound{extension}");
        File.WriteAllBytes(path, Header(extension));

        Assert.Equal(Path.GetFullPath(path), AudioFilePolicy.Validate(path));
    }

    [Fact]
    public void Validate_RejectsExtensionContentMismatch()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "sound.mp3");
        File.WriteAllBytes(path, "RIFF0000WAVE"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => AudioFilePolicy.Validate(path));
    }

    [Fact]
    public void Validate_RejectsUnsupportedExtension()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "sound.exe");
        File.WriteAllBytes(path, [1]);

        Assert.Throws<InvalidDataException>(() => AudioFilePolicy.Validate(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static byte[] Header(string extension) =>
        extension switch
        {
            ".mp3" => [(byte)'I', (byte)'D', (byte)'3', 1],
            ".wav" => "RIFF0000WAVE"u8.ToArray(),
            ".wma" =>
            [
                0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
                0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
            ],
            ".m4a" => [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p'],
            _ => throw new ArgumentOutOfRangeException(nameof(extension))
        };
}
