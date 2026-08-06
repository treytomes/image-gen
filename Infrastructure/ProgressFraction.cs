namespace ImageGen.Infrastructure;

/// <summary>Helpers for expressing progress as a fraction of a total.</summary>
public static class ProgressFraction
{
    /// <summary>Fraction complete in [0,1], or null when the total is unknown (≤ 0).</summary>
    public static double? Of(long current, long? total) =>
        total is > 0 ? Math.Clamp((double)current / total.Value, 0, 1) : null;
}
