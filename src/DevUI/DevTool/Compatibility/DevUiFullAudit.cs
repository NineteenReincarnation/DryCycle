using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Runtime-wide generic DevInterface compatibility audit.
///
/// This deliberately does not know about vanilla/RK/DryCycle feature names. It discovers every
/// instantiated DevInterface Page owned by the live DevUI, walks each real control tree through
/// <see cref="DevUiMigrationCoverage"/>, then reports only interaction protocols that the generic
/// bridge cannot drive. Active pages are still scanned every frame by the presentation controller;
/// inactive instantiated pages are sampled periodically so a developer does not have to manually
/// open every tab just to discover missing protocol families.
///
/// The second half of the audit validates both the generic mirror and the page-agnostic action
/// bridge. Every control classified as a supported structural protocol must produce a
/// <see cref="LegacyControlSnapshot"/> at the same tree path and that snapshot must have a generic
/// action route. This catches false-positive coverage without mutating room data during the audit.
/// </summary>
internal static class DevUiFullAudit
{
    private sealed class MirrorExpectation
    {
        internal string Path;
        internal string TypeName;
        internal string Id;
    }

    private const int FullScanIntervalFrames = 120;
    private const int EnumerableItemLimit = 128;
    private const int LoggedGapLimit = 32;

    private static int lastFullScanFrame = int.MinValue / 2;
    private static Page lastActivePage;
    private static string lastGapFingerprint = string.Empty;
    private static string lastMirrorFingerprint = string.Empty;
    private static int lastPageCount = -1;

    static DevUiFullAudit()
    {
        // World-space handles are intentionally retained as live scene gizmos rather than rebuilt
        // as screen-space ImGui widgets. Treat the entire Handle hierarchy as one generic protocol;
        // subclasses from vanilla or any mod inherit this behavior without per-type registrations.
        DevUiMigrationCoverage.RegisterAssignable(
            typeof(Handle),
            DevUiMigrationState.GenericAdapter,
            "Generic world-space Handle protocol retained as live scene gizmo");
    }

    /// <summary>
    /// Entry point used by the legacy presentation layer. Owner discovery is structural so the
    /// caller does not need to know which DevUINode base class/version exposes the owner member.
    /// </summary>
    internal static void ObserveAll(Page activePage)
    {
        if (activePage == null)
        {
            DevUiMigrationCoverage.Observe(null);
            return;
        }

        global::DevInterface.DevUI owner = FindOwner(activePage);
        if (owner != null)
        {
            ObserveAll(owner);
            return;
        }

        // Conservative fallback for an unexpected DevInterface build: still audit the active tree.
        DevUiMigrationCoverage.Observe(activePage);
        List<string> mirrorGaps = ValidateMirrors(new[] { activePage });
        LogAuditResult(1, DevUiMigrationCoverage.Observed, mirrorGaps);
    }

    internal static void ObserveAll(global::DevInterface.DevUI owner)
    {
        if (owner == null) return;

        Page active = owner.activePage;
        int frame = Time.frameCount;
        bool activeChanged = !ReferenceEquals(active, lastActivePage);
        bool intervalElapsed = frame - lastFullScanFrame >= FullScanIntervalFrames;
        if (!activeChanged && !intervalElapsed) return;

        lastActivePage = active;
        lastFullScanFrame = frame;

        HashSet<Page> pages = DiscoverInstantiatedPages(owner);

        // Scan inactive pages first and restore CurrentPage to the active page at the end.
        foreach (Page page in pages)
        {
            if (page == null || ReferenceEquals(page, active)) continue;
            DevUiMigrationCoverage.Observe(page);
        }
        if (active != null)
            DevUiMigrationCoverage.Observe(active);

        List<string> mirrorGaps = ValidateMirrors(pages);
        LogAuditResult(pages.Count, DevUiMigrationCoverage.Observed, mirrorGaps);
    }

    internal static void Reset()
    {
        lastFullScanFrame = int.MinValue / 2;
        lastActivePage = null;
        lastGapFingerprint = string.Empty;
        lastMirrorFingerprint = string.Empty;
        lastPageCount = -1;
    }

    private static global::DevInterface.DevUI FindOwner(DevUINode node)
    {
        if (node == null) return null;
        Type current = node.GetType();
        while (current != null)
        {
            FieldInfo[] fields;
            try
            {
                fields = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                current = current.BaseType;
                continue;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (!typeof(global::DevInterface.DevUI).IsAssignableFrom(field.FieldType)) continue;
                try
                {
                    if (field.GetValue(node) is global::DevInterface.DevUI owner)
                        return owner;
                }
                catch { }
            }

            PropertyInfo[] properties;
            try
            {
                properties = current.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                current = current.BaseType;
                continue;
            }

            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (!property.CanRead || property.GetIndexParameters().Length != 0 ||
                    !typeof(global::DevInterface.DevUI).IsAssignableFrom(property.PropertyType))
                    continue;
                try
                {
                    if (property.GetValue(node, null) is global::DevInterface.DevUI owner)
                        return owner;
                }
                catch { }
            }

            current = current.BaseType;
        }
        return null;
    }

    private static HashSet<Page> DiscoverInstantiatedPages(global::DevInterface.DevUI owner)
    {
        HashSet<Page> pages = new();
        if (owner?.activePage != null) pages.Add(owner.activePage);
        if (owner == null) return pages;

        Type current = owner.GetType();
        while (current != null)
        {
            FieldInfo[] fields;
            try
            {
                fields = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                current = current.BaseType;
                continue;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try { value = fields[i].GetValue(owner); }
                catch { continue; }
                CollectPageValues(value, pages);
            }

            current = current.BaseType;
        }

        return pages;
    }

    private static void CollectPageValues(object value, HashSet<Page> pages)
    {
        if (value == null || pages == null) return;
        if (value is Page page)
        {
            pages.Add(page);
            return;
        }
        if (value is string || value is not IEnumerable enumerable) return;

        int visited = 0;
        IEnumerator iterator = null;
        try
        {
            iterator = enumerable.GetEnumerator();
            while (visited < EnumerableItemLimit && iterator.MoveNext())
            {
                visited++;
                if (iterator.Current is Page item)
                    pages.Add(item);
            }
        }
        catch
        {
            // Live collections can invalidate while DevInterface is updating. Missing one periodic
            // sample is harmless because the active page still receives a per-frame audit.
        }
        finally
        {
            if (iterator is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch { }
            }
        }
    }

    private static List<string> ValidateMirrors(IEnumerable<Page> pages)
    {
        List<string> failures = new();
        if (pages == null) return failures;

        foreach (Page page in pages)
        {
            if (page == null) continue;

            LegacyControlSnapshot[] mirrored;
            try { mirrored = LegacyDevInterfaceBridge.CaptureRoot(page); }
            catch (Exception error)
            {
                failures.Add(
                    (page.GetType().FullName ?? page.GetType().Name) + "|<capture>|" + error.GetType().Name + ": " + error.Message);
                continue;
            }

            HashSet<string> mirroredPaths = new(StringComparer.Ordinal);
            for (int i = 0; i < mirrored.Length; i++)
            {
                LegacyControlSnapshot snapshot = mirrored[i];
                string path = snapshot?.Path;
                if (string.IsNullOrWhiteSpace(path)) continue;
                mirroredPaths.Add(path);

                if (!UniversalDevUiActionBridge.CanExecute(page, snapshot))
                {
                    failures.Add(
                        (page.GetType().FullName ?? page.GetType().Name) + "|" + path + "|" +
                        (snapshot.RuntimeType ?? string.Empty) + "|" + (snapshot.Id ?? string.Empty) +
                        "|no generic action route for " + snapshot.Kind);
                }
            }

            List<MirrorExpectation> expected = new();
            CollectMirrorExpectations(page, string.Empty, expected);
            for (int i = 0; i < expected.Count; i++)
            {
                MirrorExpectation item = expected[i];
                if (mirroredPaths.Contains(item.Path)) continue;
                failures.Add(
                    (page.GetType().FullName ?? page.GetType().Name) + "|" + item.Path + "|" +
                    item.TypeName + "|" + item.Id + "|classified generic but missing from mirror");
            }
        }

        failures.Sort(StringComparer.Ordinal);
        return failures;
    }

    private static void CollectMirrorExpectations(
        DevUINode parent,
        string parentPath,
        List<MirrorExpectation> output)
    {
        if (parent?.subNodes == null || output == null) return;

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node == null) continue;
            string path = string.IsNullOrEmpty(parentPath) ? i.ToString() : parentPath + "." + i;

            if (LegacyDevInterfaceBridge.CanAdaptNode(node))
            {
                output.Add(new MirrorExpectation
                {
                    Path = path,
                    TypeName = node.GetType().FullName ?? node.GetType().Name,
                    Id = node.IDstring ?? string.Empty
                });
            }

            // Atomic controls intentionally hide presentation-only child nodes such as labels/nubs.
            // Composite Buttons remain recursive so any dynamically-opened custom panel is audited.
            if (LegacyDevInterfaceBridge.IsAtomicAdaptedControl(node)) continue;
            CollectMirrorExpectations(node, path, output);
        }
    }

    private static void LogAuditResult(
        int pageCount,
        DevUiMigrationCoverageSnapshot snapshot,
        List<string> mirrorGaps)
    {
        snapshot ??= DevUiMigrationCoverageSnapshot.Empty;
        mirrorGaps ??= new List<string>();

        List<string> gaps = new();
        DevUiMigrationCoverageEntry[] entries = snapshot.Entries ?? Array.Empty<DevUiMigrationCoverageEntry>();
        for (int i = 0; i < entries.Length; i++)
        {
            DevUiMigrationCoverageEntry entry = entries[i];
            if (entry == null || entry.State != DevUiMigrationState.Unmapped) continue;
            gaps.Add(
                entry.Source + "|" + entry.PageType + "|" + entry.TypeName + "|" +
                entry.ExampleId + "|" + entry.AdapterNote);
        }
        gaps.Sort(StringComparer.Ordinal);

        string fingerprint = string.Join("\n", gaps);
        string mirrorFingerprint = string.Join("\n", mirrorGaps);
        if (pageCount == lastPageCount &&
            string.Equals(fingerprint, lastGapFingerprint, StringComparison.Ordinal) &&
            string.Equals(mirrorFingerprint, lastMirrorFingerprint, StringComparison.Ordinal))
            return;

        lastPageCount = pageCount;
        lastGapFingerprint = fingerprint;
        lastMirrorFingerprint = mirrorFingerprint;

        Plugin.Logger?.LogInfo(
            "DevTool full generic audit scanned " + pageCount + " instantiated page(s); " +
            snapshot.TotalTypeCount + " interactive obligations observed, " +
            snapshot.UnmappedTypeCount + " protocol gap(s), " + mirrorGaps.Count + " mirror/action gap(s).");

        int shown = Math.Min(LoggedGapLimit, gaps.Count);
        for (int i = 0; i < shown; i++)
            Plugin.Logger?.LogWarning("[DevUI protocol gap] " + gaps[i]);
        if (gaps.Count > shown)
            Plugin.Logger?.LogWarning(
                "[DevUI protocol gap] " + (gaps.Count - shown) + " additional gap(s) omitted from this log batch.");

        int mirrorShown = Math.Min(LoggedGapLimit, mirrorGaps.Count);
        for (int i = 0; i < mirrorShown; i++)
            Plugin.Logger?.LogWarning("[DevUI mirror/action gap] " + mirrorGaps[i]);
        if (mirrorGaps.Count > mirrorShown)
            Plugin.Logger?.LogWarning(
                "[DevUI mirror/action gap] " + (mirrorGaps.Count - mirrorShown) + " additional gap(s) omitted from this log batch.");
    }
}
