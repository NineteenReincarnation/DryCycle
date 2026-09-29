using System;
using System.Collections.Generic;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Owns DevInterface nodes before their constructors can throw. A failed derived constructor
/// never returns its instance to the caller, so post-construction tree cleanup alone is insufficient.
/// This hook is confined to the external DevInterface constructor boundary; normal pages pass through.
/// </summary>
internal sealed class HeadlessObjectConstructionScope : IDisposable
{
    [ThreadStatic] private static HeadlessObjectConstructionScope current;
    private static bool enabled;
    private readonly HeadlessObjectConstructionScope previousScope;
    private readonly global::DevInterface.DevUI owner;
    private readonly Page previousPage;
    private readonly List<DevUINode> ownedNodes;
    private readonly FContainer quarantine;
    private readonly bool constructingPage;
    private bool disposed;

    internal HeadlessObjectConstructionScope(
        global::DevInterface.DevUI owner, List<DevUINode> ownedNodes, FContainer quarantine,
        bool constructingPage = true)
    {
        if (!enabled)
        {
            On.DevInterface.DevUINode.ctor += ObserveConstruction;
            enabled = true;
        }

        this.owner = owner;
        this.ownedNodes = ownedNodes;
        this.quarantine = quarantine;
        this.constructingPage = constructingPage;
        previousScope = current;
        previousPage = owner.activePage;
        current = this;
    }

    private static void ObserveConstruction(
        On.DevInterface.DevUINode.orig_ctor orig, DevUINode self,
        global::DevInterface.DevUI owner, string id, DevUINode parent)
    {
        HeadlessObjectConstructionScope scope = current;
        if (scope != null && ReferenceEquals(scope.owner, owner))
        {
            scope.ownedNodes.Add(self);
            // Page/ObjectsPage hooks may consult activePage *during* construction. Assigning it
            // only after new ObjectsPage returns gives them the unrelated native tool page.
            if (scope.constructingPage && self is ObjectsPage page)
                owner.activePage = page;
        }
        orig(self, owner, id, parent);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        current = previousScope;
        owner.activePage = previousPage;
        // Includes partially constructed nodes which never reached their parent's subNodes list.
        for (int i = 0; i < ownedNodes.Count; i++)
        {
            try { HeadlessObjectCompatibilityHost.QuarantineNodeVisuals(ownedNodes[i], quarantine); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool partial object UI quarantine failed: " + error);
            }
        }
    }

    internal static void Reset()
    {
        if (!enabled) return;
        On.DevInterface.DevUINode.ctor -= ObserveConstruction;
        enabled = false;
        current = null;
    }
}
