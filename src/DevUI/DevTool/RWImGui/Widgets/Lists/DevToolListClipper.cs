using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Stack-only owner for Dear ImGui's native list clipper.
/// Keeps the unsafe lifetime in one place so large editor lists can render only visible rows
/// without creating managed garbage every frame.
/// </summary>
internal unsafe ref struct DevToolListClipper
{
    private readonly ImGuiListClipperPtr clipper;

    internal DevToolListClipper(int itemCount)
    {
        clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(itemCount < 0 ? 0 : itemCount);
    }

    internal DevToolListClipper(int itemCount, float itemHeight)
    {
        clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(itemCount < 0 ? 0 : itemCount, itemHeight);
    }

    internal readonly bool Step(out int firstVisible, out int lastVisibleExclusive)
    {
        if (!clipper.Step())
        {
            firstVisible = 0;
            lastVisibleExclusive = 0;
            return false;
        }

        firstVisible = clipper.DisplayStart;
        lastVisibleExclusive = clipper.DisplayEnd;
        return true;
    }

    public readonly void Dispose()
    {
        // Native destruction runs ImGuiListClipper's destructor, which also finalizes an active
        // clipping session. Avoid a separate End() call so early exits cannot double-finalize it.
        clipper.Destroy();
    }
}
