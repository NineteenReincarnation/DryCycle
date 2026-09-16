using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Final map-config room-record contract filter.
///
/// Vanilla MapPage.SaveMapConfig rewrites room records from the current RoomPanel set and omits every
/// World.DisabledMapRooms entry. The rebuilt config pipeline intentionally preserves unknown extension
/// lines, so an old room record could otherwise survive after that room becomes conditionally hidden,
/// is renamed, or is removed from the region. Immediately before the single atomic write, recognize
/// only the exact vanilla 8-field room-record shape and retain it only for a current visible RoomPanel.
/// Unknown/third-party records are left untouched.
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
                throw new InvalidOperationException("Player Map room-record contract filter hook was not created.");

            enabled = true;
            logger?.LogInfo("Player Map active room-record config contract filter enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map room-record config filter could not attach: " + Unwrap(error).Message);
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
        if (!enabled || lines == null || !TryGetActiveMap(target, out MapPage page))
        {
            orig(target, lines);
            return;
        }

        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world?.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
            {
                string name = page.world.DisabledMapRooms[i];
                if (!string.IsNullOrWhiteSpace(name)) disabled.Add(name.Trim());
            }
        }

        HashSet<string> visibleRooms = new(StringComparer.OrdinalIgnoreCase);
        if (page.subNodes != null)
        {
            for (int i = 0; i < page.subNodes.Count; i++)
            {
                if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
                string name = panel.roomRep.room.name;
                if (!string.IsNullOrWhiteSpace(name) && !disabled.Contains(name))
                    visibleRooms.Add(name);
            }
        }

        List<string> filtered = null;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i] ?? string.Empty;
            bool strip = TryParseVanillaRoomRecord(line, out string roomName) && !visibleRooms.Contains(roomName);
            if (!strip)
            {
                filtered?.Add(line);
                continue;
            }

            if (filtered == null)
            {
                filtered = new List<string>(Math.Max(0, lines.Count - 1));
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

    private static bool TryParseVanillaRoomRecord(string line, out string roomName)
    {
        roomName = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        int colon = line.IndexOf(':');
        if (colon <= 0) return false;

        string key = line.Substring(0, colon).Trim();
        if (key.Length == 0) return false;
        string payload = line.Substring(colon + 1).Trim();
        string[] fields = payload.Split(new[] { "><" }, StringSplitOptions.None);
        if (fields.Length < 8) return false;

        // Exact fields emitted by MapPage.SaveMapConfig:
        // canonX, canonY, devX, devY, layer, subregion, roomWidth, roomHeight.
        if (!TryFloat(fields[0]) || !TryFloat(fields[1]) || !TryFloat(fields[2]) || !TryFloat(fields[3]) ||
            !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false;

        roomName = key;
        return true;
    }

    private static bool TryFloat(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string NormalizePath(string value) =>
        (value ?? string.Empty).Replace('\\', '/').Trim();

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
