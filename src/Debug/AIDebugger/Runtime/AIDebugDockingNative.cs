using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal static class AIDebugDockingNative
{
    // ImGuiDockNodeFlags_DockSpace is intentionally part of Dear ImGui's private
    // ImGuiDockNodeFlagsPrivate_ enum and therefore is not generated into the public
    // ImGui.NET ImGuiDockNodeFlags enum. cimgui 1.91.x defines it as 1 << 10.
    private const ImGuiDockNodeFlags DockSpaceNodeFlag = (ImGuiDockNodeFlags)(1 << 10);
    private const string DockingIniHeader = "[Docking][Data]";

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

    // Dear ImGui/cimgui uses size_t here. UIntPtr is required on Rain World's x64
    // process; using uint/out uint corrupts the native ABI because size_t is 8 bytes.
    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern void igLoadIniSettingsFromMemory(IntPtr ini_data, UIntPtr ini_size);

    [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr igSaveIniSettingsToMemory(out UIntPtr out_ini_size);

    internal static void DockSpace(uint dockspaceId, Num.Vector2 size,
        ImGuiDockNodeFlags flags = ImGuiDockNodeFlags.None) =>
        igDockSpace(dockspaceId, size, flags, IntPtr.Zero);

    internal static void BuildDefault(uint dockspaceId, Num.Vector2 size)
    {
        igDockBuilderRemoveNode(dockspaceId);
        igDockBuilderAddNode(dockspaceId, DockSpaceNodeFlag);
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
        if (!ContainsDockingData(data)) return false;

        IntPtr memory = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, memory, data.Length);
            igLoadIniSettingsFromMemory(memory, new UIntPtr((uint)data.Length));
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

        IntPtr memory = igSaveIniSettingsToMemory(out UIntPtr nativeSize);
        ulong rawSize = nativeSize.ToUInt64();
        if (memory == IntPtr.Zero || rawSize == 0 || rawSize > int.MaxValue) return;

        byte[] data = new byte[(int)rawSize];
        Marshal.Copy(memory, data, 0, data.Length);

        // Compact mode also creates ordinary ImGui window settings. Never overwrite a
        // valid DockSpace layout with a compact-only ini that contains no docking tree.
        if (!ContainsDockingData(data)) return;
        File.WriteAllBytes(AIDebugSettings.LayoutPath, data);
    }

    internal static void DeleteLayout()
    {
        string path = AIDebugSettings.LayoutPath;
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool ContainsDockingData(byte[] data)
    {
        if (data == null || data.Length < DockingIniHeader.Length) return false;
        // ImGui ini syntax is ASCII-compatible even when localized window labels contain
        // UTF-8. Searching the decoded text is safe and keeps corrupted/partial files out.
        string text = Encoding.UTF8.GetString(data);
        return text.IndexOf(DockingIniHeader, StringComparison.Ordinal) >= 0;
    }
}
