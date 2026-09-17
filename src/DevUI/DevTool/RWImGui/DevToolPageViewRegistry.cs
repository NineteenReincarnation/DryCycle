using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Single registration point for all rebuilt DevTool pages.
///
/// The registry owns exactly one composite page object per ToolMode and one stable ID per page.
/// Rendering and lifecycle live on that same object, avoiding parallel Page/PageView ownership.
/// </summary>
internal static class DevToolPageViewRegistry
{
    private static readonly Dictionary<EditorToolMode, IDevToolFrontendPage> Pages = new();
    private static readonly Dictionary<string, IDevToolFrontendPage> PagesById =
        new(StringComparer.OrdinalIgnoreCase);
    private static IDevToolFrontendPage activePage;

    static DevToolPageViewRegistry()
    {
        Register(new RoomDevToolPage());
        Register(new ObjectsDevToolPage());
        Register(new SoundDevToolPage());
        Register(new TriggersDevToolPage());
        Register(new MapDevToolPage());
        Register(new DialogDevToolPage());
        Register(new RelationshipsDevToolPage());
        ValidateBuiltinCoverage();
    }

    internal static IDevToolPageView Get(EditorToolMode mode)
    {
        SynchronizeActive(mode);
        Pages.TryGetValue(mode, out IDevToolFrontendPage page);
        return page;
    }

    internal static bool TryGet(string id, out IDevToolPageView view)
    {
        view = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!PagesById.TryGetValue(id.Trim(), out IDevToolFrontendPage page)) return false;
        view = page;
        return true;
    }

    internal static string ActivePageId => activePage?.Id ?? string.Empty;
    internal static int RegisteredPageCount => Pages.Count;

    internal static void SynchronizeActive(EditorToolMode mode)
    {
        Pages.TryGetValue(mode, out IDevToolFrontendPage next);
        if (ReferenceEquals(activePage, next)) return;

        IDevToolFrontendPage previous = activePage;
        activePage = null;
        previous?.Deactivate();

        if (next == null) return;
        next.Activate();
        activePage = next;
    }

    internal static void DeactivateActive()
    {
        IDevToolFrontendPage previous = activePage;
        activePage = null;
        previous?.Deactivate();
    }

    internal static void Register(IDevToolFrontendPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (string.IsNullOrWhiteSpace(page.Id))
            throw new ArgumentException("Page id must not be empty.", nameof(page));
        if (Pages.ContainsKey(page.Mode))
            throw new InvalidOperationException("A DevTool page is already registered for mode " + page.Mode + ".");
        if (PagesById.ContainsKey(page.Id))
            throw new InvalidOperationException("A DevTool page is already registered with id '" + page.Id + "'.");

        Pages.Add(page.Mode, page);
        PagesById.Add(page.Id, page);
    }

    internal static void ResetAll()
    {
        DeactivateActive();
        foreach (IDevToolFrontendPage page in Pages.Values)
            page.Reset();
    }

    private static void ValidateBuiltinCoverage()
    {
        Array modes = Enum.GetValues(typeof(EditorToolMode));
        for (int i = 0; i < modes.Length; i++)
        {
            EditorToolMode mode = (EditorToolMode)modes.GetValue(i);
            if (!Pages.ContainsKey(mode))
                throw new InvalidOperationException("No RWImGui DevTool page is registered for mode " + mode + ".");
        }
    }
}
