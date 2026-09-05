using System;
using System.Reflection;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal static class DesertBatflySandbox
{
    internal const string UnlockValue = "DesertBatfly";
    internal static MultiplayerUnlocks.SandboxUnlockID UnlockID { get; private set; }

    private static bool enabled;
    private static object harmony;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;

        UnlockID = new MultiplayerUnlocks.SandboxUnlockID(UnlockValue, true);
        EnsureCreatureUnlockList();

        harmony = DesertBatflyRuntimePatch.Create("Anno.DesertBatfly.Sandbox");
        MethodInfo sprite = typeof(CreatureSymbol).GetMethod(
            nameof(CreatureSymbol.SpriteNameOfCreature),
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo color = typeof(CreatureSymbol).GetMethod(
            nameof(CreatureSymbol.ColorOfCreature),
            BindingFlags.Public | BindingFlags.Static);
        MethodInfo spritePrefix = typeof(DesertBatflySandbox).GetMethod(
            nameof(SpriteNamePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo colorPrefix = typeof(DesertBatflySandbox).GetMethod(
            nameof(ColorPrefix), BindingFlags.NonPublic | BindingFlags.Static);

        DesertBatflyRuntimePatch.Patch(harmony, sprite, spritePrefix);
        DesertBatflyRuntimePatch.Patch(harmony, color, colorPrefix);
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;

        DesertBatflyRuntimePatch.UnpatchSelf(harmony);
        harmony = null;

        if (MultiplayerUnlocks.CreatureUnlockList != null)
            MultiplayerUnlocks.CreatureUnlockList.RemoveAll(id => id != null && id.value == UnlockValue);

        UnlockID?.Unregister();
        UnlockID = null;
    }

    private static void EnsureCreatureUnlockList()
    {
        if (MultiplayerUnlocks.CreatureUnlockList == null || UnlockID == null) return;
        foreach (MultiplayerUnlocks.SandboxUnlockID id in MultiplayerUnlocks.CreatureUnlockList)
            if (id != null && id.value == UnlockValue) return;

        int flyIndex = MultiplayerUnlocks.CreatureUnlockList.FindIndex(id => id == MultiplayerUnlocks.SandboxUnlockID.Fly);
        if (flyIndex < 0) MultiplayerUnlocks.CreatureUnlockList.Add(UnlockID);
        else MultiplayerUnlocks.CreatureUnlockList.Insert(flyIndex + 1, UnlockID);
    }

    // MultiplayerUnlocks.SymbolDataForSandboxUnlock automatically maps any entry in
    // CreatureUnlockList to a CreatureTemplate.Type with the same string. Because
    // both IDs are "DesertBatfly", vanilla sandbox spawning and blue-token progress
    // need no custom spawning or save format.
    private static bool SpriteNamePrefix(IconSymbol.IconSymbolData iconData, ref string __result)
    {
        if (iconData.critType != DesertBatflyDefinition.CreatureType) return true;
        __result = "Kill_Bat";
        return false;
    }

    private static bool ColorPrefix(IconSymbol.IconSymbolData iconData, ref Color __result)
    {
        if (iconData.critType != DesertBatflyDefinition.CreatureType) return true;
        __result = new Color(0.67f, 0.45f, 0.26f);
        return false;
    }
}
