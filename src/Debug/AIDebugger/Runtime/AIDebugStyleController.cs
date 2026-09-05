using ImGuiNET;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal static class AIDebugStyleController
{
    private static float appliedScale = 1f;

    internal static void Apply()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        io.FontGlobalScale = Mathf.Clamp(AIDebugSettings.FontScale, 0.75f, 1.75f);

        float wanted = Mathf.Clamp(AIDebugSettings.UiScale, 0.75f, 1.75f);
        if (Mathf.Abs(wanted - appliedScale) <= 0.001f) return;
        float ratio = wanted / Mathf.Max(0.001f, appliedScale);
        ImGui.GetStyle().ScaleAllSizes(ratio);
        appliedScale = wanted;
    }

    internal static void Reset() => appliedScale = 1f;
}
