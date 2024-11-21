namespace Jellyfin.Plugin.SegmentRecognition.Tests;

using Xunit;

/// <summary>
/// Tests warnings.
/// </summary>
public class TestFlags
{
    /// <summary>
    /// Tests empty flag serialization.
    /// </summary>
    [Fact]
    public void TestEmptyFlagSerialization()
    {
        WarningManager.Clear();
        Assert.Equal("None", WarningManager.GetWarnings());
    }

    /// <summary>
    /// Tests single flag serialization.
    /// </summary>
    [Fact]
    public void TestSingleFlagSerialization()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
        Assert.Equal("UnableToAddSkipButton", WarningManager.GetWarnings());
    }

    /// <summary>
    /// Tests double flag serialization.
    /// </summary>
    [Fact]
    public void TestDoubleFlagSerialization()
    {
        WarningManager.Clear();
        WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);
        WarningManager.SetFlag(PluginWarning.InvalidChromaprintFingerprint);

        Assert.Equal(
            "UnableToAddSkipButton, InvalidChromaprintFingerprint",
            WarningManager.GetWarnings());
    }
}
