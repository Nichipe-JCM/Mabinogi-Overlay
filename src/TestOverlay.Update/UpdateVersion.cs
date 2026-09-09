using System.Globalization;
using System.Text.RegularExpressions;

namespace TestOverlay.Update;

public sealed class UpdateVersion : IComparable<UpdateVersion>
{
    private static readonly Regex Pattern = new(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly int[] _numbers;
    private readonly string[] _suffix;
    private UpdateVersion(string text, int[] numbers, string[] suffix) { Text = text; _numbers = numbers; _suffix = suffix; }
    public string Text { get; }
    public static UpdateVersion Parse(string text)
    {
        if (text.Length > 100) throw new FormatException("Version is too long.");
        var match = Pattern.Match(text);
        if (!match.Success) throw new FormatException("Unsupported version format.");
        var numbers = Enumerable.Range(1, 4).Select(i => match.Groups[i].Success ? int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0).ToArray();
        var suffix = match.Groups[5].Success ? match.Groups[5].Value.Split('.') : [];
        if (suffix.Any(s => s.Length > 1 && s[0] == '0' && s.All(char.IsAsciiDigit))) throw new FormatException("Invalid numeric prerelease identifier.");
        return new UpdateVersion(text.StartsWith('v') ? text[1..] : text, numbers, suffix);
    }
    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 4; i++) { var c = _numbers[i].CompareTo(other._numbers[i]); if (c != 0) return c; }
        if (_suffix.Length == 0 || other._suffix.Length == 0) return (_suffix.Length == 0 ? 1 : 0).CompareTo(other._suffix.Length == 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(_suffix.Length, other._suffix.Length); i++)
        {
            var a = _suffix[i]; var b = other._suffix[i];
            var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            var c = an && bn ? (a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b))
                : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(a, b);
            if (c != 0) return c;
        }
        return _suffix.Length.CompareTo(other._suffix.Length);
    }
}
