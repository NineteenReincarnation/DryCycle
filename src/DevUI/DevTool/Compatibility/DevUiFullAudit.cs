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
/// </summary>
internal static class DevUiFullAudit
{
    private const int FullScanIntervalFrames = 120;
    private const int EnumerableItemLimit = 128;
    private const int LoggedGapLimit = 32;

    private static int lastFullScanFrame = int.MinValue / 2;
    private static Page lastActivePage;
    private static string lastGapFingerprint = string.Empty;
    private static int lastPageCount = -1;

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

        LogAuditResult(pages.Count, DevUiMigrationCoverage.Observed);
    }

    internal static void Reset()
    {
        lastFullScanFrame = int.MinValue / 2;
        lastActivePage = null;
        lastGapFingerprint = string.Empty;
        lastPageCount = -1;
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

    private static void LogAuditResult(int pageCount, DevUiMigrationCoverageSnapshot snapshot)
    {
        snapshot ??= DevUiMigrationCoverageSnapshot.Empty;
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
        if (pageCount == lastPageCount && string.Equals(fingerprint, lastGapFingerprint, StringComparison.Ordinal))
            return;

        lastPageCount = pageCount;
        lastGapFingerprint = fingerprint;

        Plugin.Logger?.LogInfo(
            "DevTool full generic audit scanned " + pageCount + " instantiated page(s); " +
            snapshot.TotalTypeCount + " interactive obligations observed, " +
            snapshot.UnmappedTypeCount + " protocol gap(s) remain.");

        int shown = Math.Min(LoggedGapLimit, gaps.Count);
        for (int i = 0; i < shown; i++)
            Plugin.Logger?.LogWarning("[DevUI protocol gap] " + gaps[i]);
        if (gaps.Count > shown)
            Plugin.Logger?.LogWarning(
                "[DevUI protocol gap] " + (gaps.Count - shown) + " additional gap(s) omitted from this log batch.");
    }
}
