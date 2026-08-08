using Xunit;

namespace Jellyfin.Plugin.SegmentRecognition.Tests.TestHarness;

/// <summary>
/// Collection for tests that install a <see cref="PluginConfigScope"/>. Membership disables
/// parallel execution between them, which is required because <c>Plugin.Instance</c> is global
/// process state.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginStateCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "PluginState";
}
