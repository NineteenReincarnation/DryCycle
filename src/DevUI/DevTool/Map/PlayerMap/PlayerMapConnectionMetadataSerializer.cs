namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Legacy plugin identifier retained so external source references do not break. Connection metadata
/// generation now lives inside PlayerMapConfigBuildPipeline and is emitted in the same atomic map
/// document transaction as rooms, Def_Mat and migration streams.
/// </summary>
public static class PlayerMapConnectionMetadataSerializerPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.ConnectionMetadata";
    public const string PluginName = "DryCycle Player Map Connection Metadata (retired)";
    public const string PluginVersion = global::DryCycle.Plugin.Version;
}

internal static class PlayerMapConnectionMetadataSerializer
{
}
