using System.Windows;

namespace TestOverlay.App.Models;

public sealed record BuffIconMatch(
    string NameKey,
    Rect Bounds,
    double StructureScore,
    bool IsActive,
    double StateConfidence,
    string TemplateId = "");

public sealed record BuffWindowDetectionResult(
    Rect Roi,
    IReadOnlyList<BuffIconMatch> Matches);

public sealed record TuairimDetectionResult(
    Rect Roi,
    Rect Bounds,
    double Score);
