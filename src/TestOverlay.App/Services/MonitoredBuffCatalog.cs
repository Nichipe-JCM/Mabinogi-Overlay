namespace TestOverlay.App.Services;

public static class MonitoredBuffCatalog
{
    public const string BattleOverture = "monitor.buff.battle.overture";
    public const string MarchSong = "monitor.buff.march.song";
    public const string Vivace = "monitor.buff.vivace";
    public const string HarvestSong = "monitor.buff.harvest.song";
    public const string DivineLink = "monitor.buff.divine.link";
    public const string ConditionSupport = "monitor.buff.condition.support";
    public const string PurificationWave = "monitor.buff.purification.wave";

    public static IReadOnlyList<string> MusicBuffNameKeys { get; } =
    [
        BattleOverture,
        MarchSong,
        Vivace,
        HarvestSong
    ];

    public static IReadOnlyList<string> StatusBuffNameKeys { get; } =
    [
        DivineLink,
        ConditionSupport,
        PurificationWave
    ];

    public static IReadOnlyList<string> AllBuffNameKeys { get; } =
        MusicBuffNameKeys.Concat(StatusBuffNameKeys).ToArray();

    public static bool IsMusicBuff(string nameKey) => MusicBuffNameKeys.Contains(nameKey);

    public static bool IsStatusBuff(string nameKey) => StatusBuffNameKeys.Contains(nameKey);
}
