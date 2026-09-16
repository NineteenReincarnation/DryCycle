namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Legacy plugin identifier retained for source compatibility. Coordinate normalization is now part
/// of the single PlayerMapConfigBuildPipeline snapshot transaction and never rewrites a just-saved
/// file as a second pass.
/// </summary>
public static class PlayerMapNormalizedConfigSerializerPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.NormalizedConfig";
    public const string PluginName = "DryCycle Player Map Normalized Config (retired)";
    public const string PluginVersion = global::DryCycle.Plugin.Version;
}

internal static class PlayerMapNormalizedConfigSerializer
{
}
