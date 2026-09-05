using System;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal static class AIDebugDockingNative
{
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockSpace(uint dockspace_id, Num.Vector2 size,
        ImGuiDockNodeFlags flags, IntPtr window_class);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderAddNode(uint node_id, ImGuiDockNodeFlags flags);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderRemoveNode(uint node_id);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodeSize(uint node_id, Num.Vector2 size);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderSplitNode(uint node_id, ImGuiDir split_dir,
        float size_ratio_for_node_at_dir, out uint out_id_at_dir, out uint out_id_at_opposite_dir);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void igDockBuilderDockWindow(string window_name, uint node_id);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderFinish(uint node_id);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void igLoadIniSettingsFromMemory(IntPtr ini_data, uint ini_size);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr igSaveIniSettingsToMemory(out uint out_ini_size);

    internal static void DockSpace(uint dockspaceId, Num.Vector2 size,
        ImGuiDockNodeFlags flags = ImGuiDockNodeFlags.None) =>
        igDockSpace(dockspaceId, size, flags, IntPtr.Zero);

    internal static void BuildDefault(uint dockspaceId, Num.Vector2 size)
    {
        igDockBuilderRemoveNode(dockspaceId);
        igDockBuilderAddNode(dockspaceId, ImGuiDockNodeFlags.DockSpace);
        igDockBuilderSetNodeSize(dockspaceId, size);

        uint left, rest;
        igDockBuilderSplitNode(dockspaceId, ImGuiDir.Left, 0.21f, out left, out rest);
        uint right, centerBottom;
        igDockBuilderSplitNode(rest, ImGuiDir.Right, 0.30f, out right, out centerBottom);
        uint bottom, center;
        igDockBuilderSplitNode(centerBottom, ImGuiDir.Down, 0.31f, out bottom, out center);

        // The visible label can change language; the ### suffix gives every window a
        // stable ImGui ID, so these English bootstrap names still dock localized windows.
        igDockBuilderDockWindow("Entity Browser###AIEntityBrowser", left);
        igDockBuilderDockWindow("Inspector###AIInspector", right);
        igDockBuilderDockWindow("Timeline###AITimeline", bottom);
        igDockBuilderDockWindow("Events###AIEvents", bottom);
        igDockBuilderDockWindow("Decision Stack###AIDecision", center);
        igDockBuilderDockWindow("Utility###AIUtility", center);
        igDockBuilderDockWindow("Perception / Tracker###AIPerception", center);
        igDockBuilderDockWindow("Path / Control###AIPath", center);
        igDockBuilderDockWindow("Compare###AICompare", center);
        igDockBuilderDockWindow("Candidates###AICandidates", center);
        igDockBuilderDockWindow("Captures / Breakpoints###AICaptures", right);
        igDockBuilderDockWindow("Settings###AISettings", right);
        igDockBuilderFinish(dockspaceId);
    }

    internal static bool LoadLayout()
    {
        string path = AIDebugSettings.LayoutPath;
        if (!File.Exists(path)) return false;
        byte[] data = File.ReadAllBytes(path);
        if (data.Length == 0) return false;
        IntPtr memory = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, memory, data.Length);
            igLoadIniSettingsFromMemory(memory, checked((uint)data.Length));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        return true;
    }

    internal static void SaveLayout()
    {
        string directory = Path.GetDirectoryName(AIDebugSettings.LayoutPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        IntPtr memory = igSaveIniSettingsToMemory(out uint size);
        if (memory == IntPtr.Zero || size == 0) return;
        byte[] data = new byte[checked((int)size)];
        Marshal.Copy(memory, data, 0, data.Length);
        File.WriteAllBytes(AIDebugSettings.LayoutPath, data);
    }

    internal static void DeleteLayout()
    {
        string path = AIDebugSettings.LayoutPath;
        if (File.Exists(path)) File.Delete(path);
    }
}
