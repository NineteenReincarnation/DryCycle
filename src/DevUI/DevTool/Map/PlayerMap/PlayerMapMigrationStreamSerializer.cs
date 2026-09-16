namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Legacy plugin identifier retained for source compatibility. Migration-stream snapshot translation
/// now runs inside PlayerMapConfigBuildPipeline before the single atomic map-config commit.
/// </summary>
public static class PlayerMapMigrationStreamSerializerPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.MigrationStreamSerializer";
    public const string PluginName = "DryCycle Player Map Migration Stream Serializer (retired)";
    public const string PluginVersion = global::DryCycle.Plugin.Version;
}

internal static class PlayerMapMigrationStreamSerializer
{
}
