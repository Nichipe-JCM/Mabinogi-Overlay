namespace TestOverlay.App.Models;

public enum OverlayElementKind
{
    Quickslot,
    InternalBuffTimer,
    TuairimGauge,
    AlertNotification
}

internal static class BuiltInOverlayElementIds
{
    public const int InternalBuffTimer = -1;
    public const int TuairimGauge = -2;
    public const int AlertNotification = -3;

    public static int For(OverlayElementKind kind) => kind switch
    {
        OverlayElementKind.InternalBuffTimer => InternalBuffTimer,
        OverlayElementKind.TuairimGauge => TuairimGauge,
        OverlayElementKind.AlertNotification => AlertNotification,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Quickslots do not have a reserved ID.")
    };
}
