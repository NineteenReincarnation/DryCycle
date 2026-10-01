using System.Collections.Generic;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Minimal DevInterface lifetime anchor for page-less rebuilt tools.
///
/// DevUI requires activePage to remain a Page, but rebuilt native tools do not need any legacy
/// page controls or page-specific Update work. The anchor remembers the virtual tool mode so
/// EditorSession can resolve the correct document/history identity without constructing the
/// corresponding vanilla page. Page's base constructor creates common chrome, which is retired
/// immediately.
/// </summary>
internal sealed class NativeToolAnchorPage : Page
{
    internal NativeToolAnchorPage(
        global::DevInterface.DevUI owner,
        EditorToolMode toolMode)
        : base(owner, "DryCycle_Native_Tool_Anchor", null, "DryCycle Native Tool")
    {
        ToolMode = toolMode;
        if (subNodes != null)
        {
            for (int i = subNodes.Count - 1; i >= 0; i--)
            {
                try { subNodes[i]?.ClearSprites(); }
                catch { }
            }
            subNodes.Clear();
        }

        tempNodes = new List<DevUINode>();
        initRefresh = false;
    }

    internal EditorToolMode ToolMode { get; }

    public override void Refresh()
    {
        // There is no legacy presentation to materialize on this anchor. Concrete legacy pages are
        // constructed explicitly by NativeToolScheduler when compatibility presentation is needed.
        initRefresh = false;
    }
}