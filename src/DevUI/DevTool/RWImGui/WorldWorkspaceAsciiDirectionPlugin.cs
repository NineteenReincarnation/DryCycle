using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Forces the World Workspace inspector to use font-safe ASCII direction labels.
/// Some RWImGui font atlases still render the Unicode arrow glyphs as '?', even when
/// the rest of the localized text is available. This presentation-only hook keeps the
/// topology semantics unchanged while making World Links and Nodes readable everywhere.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldWorkspaceAsciiDirectionPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldWorkspaceAsciiDirections";
    public const string PluginName = "DryCycle DevTool World Workspace ASCII Directions";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldWorkspaceAsciiDirections.Enable(Logger);

    private void OnDisable() => WorldWorkspaceAsciiDirections.Disable();
}

internal static class WorldWorkspaceAsciiDirections
{
    private delegate string OrigDirectionGlyph(WorldConnectionDirection direction);
    private delegate string HookDirectionGlyph(OrigDirectionGlyph orig, WorldConnectionDirection direction);

    private delegate void OrigDrawRoomNodes(EditorMapPresentationSnapshot snapshot, EditorMapRoomSnapshot room);
    private delegate void HookDrawRoomNodes(
        OrigDrawRoomNodes orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room);

    private static readonly HookDirectionGlyph DirectionGlyphHookDelegate = DirectionGlyphHook;
    private static readonly HookDrawRoomNodes DrawRoomNodesHookDelegate = DrawRoomNodesHook;

    private static ManualLogSource log;
    private static IDisposable directionGlyphHook;
    private static IDisposable drawRoomNodesHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type workspaceType = typeof(WorldWorkspaceView);
            MethodInfo directionGlyph = workspaceType.GetMethod(
                "DirectionGlyph",
                flags,
                null,
                new[] { typeof(WorldConnectionDirection) },
                null);
            MethodInfo drawRoomNodes = workspaceType.GetMethod(
                "DrawRoomNodes",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(EditorMapRoomSnapshot) },
                null);

            if (directionGlyph == null)
                throw new MissingMethodException("WorldWorkspaceView.DirectionGlyph was not found.");
            if (drawRoomNodes == null)
                throw new MissingMethodException("WorldWorkspaceView.DrawRoomNodes was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            directionGlyphHook = constructor.Invoke(new object[] { directionGlyph, DirectionGlyphHookDelegate }) as IDisposable;
            drawRoomNodesHook = constructor.Invoke(new object[] { drawRoomNodes, DrawRoomNodesHookDelegate }) as IDisposable;
            enabled = true;
            log?.LogInfo("World Workspace ASCII direction labels enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("World Workspace ASCII direction labels could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try
        {
            drawRoomNodesHook?.Dispose();
        }
        catch
        {
        }
        finally
        {
            drawRoomNodesHook = null;
        }

        try
        {
            directionGlyphHook?.Dispose();
        }
        catch
        {
        }
        finally
        {
            directionGlyphHook = null;
        }

        enabled = false;
        log = null;
    }

    private static string DirectionGlyphHook(OrigDirectionGlyph orig, WorldConnectionDirection direction) =>
        direction switch
        {
            WorldConnectionDirection.AToB => "->",
            WorldConnectionDirection.BToA => "<-",
            _ => "<->"
        };

    private static void DrawRoomNodesHook(
        OrigDrawRoomNodes orig,
        EditorMapPresentationSnapshot snapshot,
        EditorMapRoomSnapshot room)
    {
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        if (nodes.Length == 0)
        {
            DevToolWidgets.MutedText(DevToolUiSettings.T("没有节点数据。", "No node data."));
            return;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            EditorMapRoomNodeSnapshot node = nodes[i];
            string target = node.ConnectedRoomIndex >= 0
                ? FindRoom(snapshot, node.ConnectedRoomIndex)?.Name ?? node.ConnectedRoomIndex.ToString()
                : DevToolUiSettings.T("未连接", "Disconnected");

            string line = node.NodeIndex + " · " + node.Type;
            if (node.Exit) line += "  ->  " + target;
            ImGui.TextUnformatted(line);
        }
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            if (rooms[i]?.RoomIndex == roomIndex)
                return rooms[i];
        }
        return null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
