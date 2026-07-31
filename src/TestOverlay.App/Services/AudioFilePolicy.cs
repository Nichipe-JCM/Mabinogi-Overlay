using System.IO;

namespace TestOverlay.App.Services;

public static class AudioFilePolicy
{
    public const long MaximumAudioBytes = 50 * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".wma", ".m4a" };
    private static readonly byte[] WmaHeader =
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    ];

    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("No audio file was selected.");
        }

        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException("Supported audio formats are MP3, WAV, WMA, and M4A.");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The selected audio file does not exist.", fullPath);
        }
        if (info.Length <= 0 || info.Length > MaximumAudioBytes)
        {
            throw new InvalidDataException("The selected audio file must be between 1 byte and 50 MB.");
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(fullPath);
        var read = stream.Read(header);
        if (!HeaderMatches(extension, header[..read]))
        {
            throw new InvalidDataException(
                "The selected file contents do not match the expected audio format.");
        }

        return fullPath;
    }

    public static bool IsSupportedExtension(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    private static bool HeaderMatches(string extension, ReadOnlySpan<byte> header) =>
        extension.ToLowerInvariant() switch
        {
            ".wav" => header.Length >= 12 &&
                      header[..4].SequenceEqual("RIFF"u8) &&
                      header.Slice(8, 4).SequenceEqual("WAVE"u8),
            ".mp3" => header.Length >= 3 &&
                      (header[..3].SequenceEqual("ID3"u8) ||
                       (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)),
            ".wma" => header.Length >= 16 &&
                      header.SequenceEqual(WmaHeader),
            ".m4a" => header.Length >= 8 && header.Slice(4, 4).SequenceEqual("ftyp"u8),
            _ => false
        };
}
