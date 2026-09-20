using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Drains migration-stream commands at the same explicit main-thread boundary as ordinary Player Map
/// commands. No PlayerMapCommandQueue method is RuntimeDetoured.
/// </summary>
internal static class PlayerMapMigrationCommandBridge
{
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        logger?.LogInfo("Player Map migration command bridge enabled through direct queue processing; no self-detour attached.");
    }

    internal static void Disable()
    {
        PlayerMapMigrationCommandQueue.Clear();
        enabled = false;
    }

    internal static void Process(EditorSession session)
    {
        if (!enabled)
        {
            if (session == null) PlayerMapMigrationCommandQueue.Clear();
            return;
        }

        PlayerMapMigrationStreamRuntime.Process(session);
    }
}
