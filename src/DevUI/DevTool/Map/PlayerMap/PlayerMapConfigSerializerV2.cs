using DevInterface;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Legacy compatibility name retained for source compatibility. The old V2 detour has been retired;
/// map configuration is now built in one pass by PlayerMapConfigBuildPipeline.
/// </summary>
public static class PlayerMapConfigSerializerV2Plugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.ConfigSerializerV2";
    public const string PluginName = "DryCycle Player Map Config Serializer V2 (retired)";
    public const string PluginVersion = global::DryCycle.Plugin.Version;
}

internal static class PlayerMapConfigSerializerV2
{
    internal static bool Save(MapPage page, PlayerMapSessionState state, out string error) =>
        PlayerMapConfigBuildPipeline.Save(page, state, out error);
}
