using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class BuffVisualCatalog
{
    private static readonly IReadOnlyDictionary<string, string> ActiveIconPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MonitoredBuffCatalog.BattleOverture] = "/Image/BattlefieldOn.png",
            [MonitoredBuffCatalog.MarchSong] = "/Image/MarchOn.png",
            [MonitoredBuffCatalog.Vivace] = "/Image/VivaceOn.png",
            [MonitoredBuffCatalog.HarvestSong] = "/Image/RichyearOn.png",
            [MonitoredBuffCatalog.DivineLink] = "/Image/DivineLinkOn.png",
            [MonitoredBuffCatalog.ConditionSupport] = "/Image/ConditionSupportOn.png",
            [MonitoredBuffCatalog.PurificationWave] = "/Image/PurificationWaveOn.png",
            [MonitoredBuffCatalog.Hamjji] = "/Image/HamjjiOn.png"
        };

    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);

    public static BitmapSource ActiveIcon(string nameKey)
    {
        if (Cache.TryGetValue(nameKey, out var cached))
        {
            return cached;
        }

        var path = ActiveIconPaths.GetValueOrDefault(nameKey, "/Image/Logo.png");
        var image = new BitmapImage(new Uri($"pack://application:,,,{path}", UriKind.Absolute));
        image.Freeze();
        Cache[nameKey] = image;
        return image;
    }
}
