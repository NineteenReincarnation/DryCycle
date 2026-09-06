using ImGuiNET;

namespace DryCycle.Debugging.AI;

// Every Dear ImGui context defaults to a relative "imgui.ini". Rain World can host
// multiple ImGui-based mods in the same process, so sharing that default file can import
// stale/off-screen window state or make different mods overwrite each other's layout.
// The Observatory already owns an explicit UTF-8 layout file through
// AIDebugDockingNative; disable Dear ImGui's automatic disk path before first NewFrame.
internal static class AIDebugImGuiIniIsolation
{
    internal static unsafe void DisableAutomaticPersistence(ImGuiIOPtr io)
    {
        if (io.NativePtr == null) return;
        io.NativePtr->IniFilename = null;
    }
}
