using System.Collections.Generic;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Minimal DevInterface lifetime anchor for page-less rebuilt tools.
///
/// DevUI requires activePage to remain a Page, but native Sound/Trigger do not need any legacy page
/// controls or page-specific Update work. Page's base constructor creates the common page chrome, so
/// retire those nodes immediately and retain only the owner/document lifetime relationship.
/// </summary>
internal sealed class NativeToolAnchorPage : Page
{
    internal NativeToolAnchorPage(global::DevInterface.DevUI owner)
        : base(owner, "DryCycle_Native_Tool_Anchor", null, "DryCycle Native Tool")
    {
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

    public override void Refresh()
    {
        // There is no legacy presentation to materialize on this anchor. Sound/Trigger legacy pages
        // are constructed explicitly by NativeToolScheduler when compatibility presentation is needed.
        initRefresh = false;
    }
}
