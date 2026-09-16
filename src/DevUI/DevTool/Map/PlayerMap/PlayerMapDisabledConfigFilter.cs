using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Final map-config contract filter for conditional hidden rooms.
///
/// Vanilla MapPage.SaveMapConfig omits every room in World.DisabledMapRooms. The rebuilt config
/// pipeline already excludes those rooms from freshly generated room/connection blocks, but its
/// extension-preserving source merge can otherwise retain a stale room record that existed in an
/// older map file. Filter that exact record shape immediately before the single atomic write.
/// Unknown/third-party records remain byte-for-byte represented as lines in the outgoing document.
/// </summary>
internal static class PlayerMapDisabledConfigFilter
{
    private delegate void OrigAtomicWriteAllLines(string target, IReadOnlyList<string> lines);
    private delegate void HookAtomicWriteAllLines(OrigAtomicWriteAllLines orig, string target, IReadOnlyList<string> lines);

    private static readonly HookAtomicWriteAllLines AtomicWriteHookDelegate = AtomicWriteHook;
    private static IDisposable atomicWriteHook;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo target = typeof(PlayerMapConfigBuildPipeline).GetMethod(
                "AtomicWriteAllLines",
                flags,
                null,
                new[] { typeof(string), typeof(IReadOnlyList<string>) },
                null);
            if (target == null)
                throw new MissingMethodException("PlayerMapConfigBuildPipeline.AtomicWriteAllLines was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            atomicWriteHook = constructor.Invoke(new object[] { target, AtomicWriteHookDelegate }) as IDisposable;
            if (atomicWriteHook == null)
                throw new InvalidOperationException("Player Map disabled-room config filter hook was not created.");

            enabled = true;
            logger?.LogInfo("Player Map disabled-room config contract filter enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map disabled-room config filter could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { atomicWriteHook?.Dispose(); }
        catch { }
        atomicWriteHook = null;
        enabled = false;
    }

    private static void AtomicWriteHook(OrigAtomicWriteAllLines orig, string target, IReadOnlyList<string> lines)
    {
        if (!enabled || lines == null || !TryGetActiveMap(target, out MapPage page) ||
            page.world?.DisabledMapRooms == null || page.world.DisabledMapRooms.Count == 0)
        {
            orig(target, lines);
            return;
        }

        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
        {
            string room = page.world.DisabledMapRooms[i];
            if (!string.IsNullOrWhiteSpace(room)) disabled.Add(room.Trim());
        }
        if (disabled.Count == 0)
        {
            orig(target, lines);
            return;
        }

        List<string> filtered = null;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i] ?? string.Empty;
            if (!IsDisabledRoomRecord(line, disabled))
            {
                filtered?.Add(line);
                continue;
            }

            if (filtered == null)
            {
                filtered = new List<string>(lines.Count - 1);
                for (int j = 0; j < i; j++) filtered.Add(lines[j] ?? string.Empty);
            }
        }

        orig(target, filtered ?? lines);
    }

    private static bool TryGetActiveMap(string target, out MapPage page)
    {
        page = DevToolSessionHub.Current?.Owner?.activePage as MapPage;
        if (page == null || string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(page.filePath))
            return false;
        return string.Equals(
            NormalizePath(target),
            NormalizePath(page.filePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisabledRoomRecord(string line, HashSet<string> disabled)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        int colon = line.IndexOf(':');
        if (colon <= 0) return false;

        string key = line.Substring(0, colon).Trim();
        if (!disabled.Contains(key)) return false;

        // A map room record always uses the canonical/dev-position "><" payload. Requiring that
        // shape prevents a third-party record that happens to reuse a room name as a key from being
        // removed by this compatibility filter.
        string payload = line.Substring(colon + 1);
        return payload.IndexOf("><", StringComparison.Ordinal) >= 0;
    }

    private static string NormalizePath(string value) =>
        (value ?? string.Empty).Replace('\\', '/').Trim();

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
