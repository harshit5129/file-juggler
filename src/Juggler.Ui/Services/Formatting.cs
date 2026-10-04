namespace Juggler.Ui.Services;

/// <summary>
/// Presentation-only formatting. Lives here rather than on a window so view models and views can
/// both use it without depending on each other.
/// </summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Human-readable byte size, e.g. <c>14.29 GB</c>. Matches the status bar.</summary>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:N2} {Units[unit]}";
    }

    /// <summary>Byte size for a condition bound, where blank means "any".</summary>
    public static string SizeOrAny(long? bytes) => bytes is null ? "any" : Bytes(bytes.Value);

    /// <summary>Middle-truncates a long path so the filename stays readable.</summary>
    public static string Ellipsize(string value, int max = 72)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max || max < 2)
        {
            return value;
        }

        return "…" + value[(value.Length - (max - 1))..];
    }
}
