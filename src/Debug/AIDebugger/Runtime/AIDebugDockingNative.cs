using System;
using System.Runtime.InteropServices;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.Debugging.AI;

internal static class AIDebugDockingNative
{
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
}
