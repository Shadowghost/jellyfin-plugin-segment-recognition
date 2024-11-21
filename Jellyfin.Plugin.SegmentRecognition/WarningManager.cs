namespace Jellyfin.Plugin.SegmentRecognition;

/// <summary>
/// Warning manager.
/// </summary>
public static class WarningManager
{
    private static PluginWarning _warnings;

    /// <summary>
    /// Set warning.
    /// </summary>
    /// <param name="warning">Warning.</param>
    public static void SetFlag(PluginWarning warning)
    {
        _warnings |= warning;
    }

    /// <summary>
    /// Clear warnings.
    /// </summary>
    public static void Clear()
    {
        _warnings = PluginWarning.None;
    }

    /// <summary>
    /// Get warnings.
    /// </summary>
    /// <returns>Warnings.</returns>
    public static string GetWarnings()
    {
        return _warnings.ToString();
    }
}
