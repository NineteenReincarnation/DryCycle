using DryCycle.Token;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

/// <summary>
/// Integrates RopeSpear with Rain World's native Arena Sandbox pipeline. The unlock
/// ID intentionally has the same value as the AbstractObjectType ("RopeSpear"), so
/// vanilla token persistence and symbol lookup remain compatible with the item.
/// </summary>
internal static class RopeSpearSandboxRuntime
{
    private static readonly Color SandboxIconColor = new(0.78f, 0.60f, 0.24f);

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        if (DryCycleTokenRuntime.RopeSpearUnlock == null || RopeSpearHooks.ObjectType == null)
        {
            Plugin.Logger?.LogError(
                "RopeSpear sandbox registration requires RopeSpear object and token IDs first.");
            return;
        }

        _enabled = true;
        EnsureUnlockListed();

        On.MultiplayerUnlocks.SymbolDataForSandboxUnlock += MultiplayerUnlocks_SymbolDataForSandboxUnlock;
        On.MultiplayerUnlocks.SandboxUnlockForSymbolData += MultiplayerUnlocks_SandboxUnlockForSymbolData;
        On.SandboxGameSession.SpawnItems += SandboxGameSession_SpawnItems;
        On.ItemSymbol.SpriteNameForItem += ItemSymbol_SpriteNameForItem;
        On.ItemSymbol.ColorForItem += ItemSymbol_ColorForItem;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.ItemSymbol.ColorForItem -= ItemSymbol_ColorForItem;
        On.ItemSymbol.SpriteNameForItem -= ItemSymbol_SpriteNameForItem;
        On.SandboxGameSession.SpawnItems -= SandboxGameSession_SpawnItems;
        On.MultiplayerUnlocks.SandboxUnlockForSymbolData -= MultiplayerUnlocks_SandboxUnlockForSymbolData;
        On.MultiplayerUnlocks.SymbolDataForSandboxUnlock -= MultiplayerUnlocks_SymbolDataForSandboxUnlock;

        MultiplayerUnlocks.SandboxUnlockID unlock = DryCycleTokenRuntime.RopeSpearUnlock;
        if (unlock != null && MultiplayerUnlocks.ItemUnlockList != null)
        {
            MultiplayerUnlocks.ItemUnlockList.Remove(unlock);
        }

        _enabled = false;
    }

    private static void EnsureUnlockListed()
    {
        MultiplayerUnlocks.SandboxUnlockID unlock = DryCycleTokenRuntime.RopeSpearUnlock;
        if (unlock == null || MultiplayerUnlocks.ItemUnlockList == null)
        {
            return;
        }

        if (!MultiplayerUnlocks.ItemUnlockList.Contains(unlock))
        {
            MultiplayerUnlocks.ItemUnlockList.Add(unlock);
        }
    }

    private static IconSymbol.IconSymbolData MultiplayerUnlocks_SymbolDataForSandboxUnlock(
        On.MultiplayerUnlocks.orig_SymbolDataForSandboxUnlock orig,
        MultiplayerUnlocks.SandboxUnlockID unlockID)
    {
        if (unlockID == DryCycleTokenRuntime.RopeSpearUnlock && RopeSpearHooks.ObjectType != null)
        {
            return new IconSymbol.IconSymbolData(
                CreatureTemplate.Type.StandardGroundCreature,
                RopeSpearHooks.ObjectType,
                0);
        }

        return orig(unlockID);
    }

    private static MultiplayerUnlocks.SandboxUnlockID MultiplayerUnlocks_SandboxUnlockForSymbolData(
        On.MultiplayerUnlocks.orig_SandboxUnlockForSymbolData orig,
        IconSymbol.IconSymbolData data)
    {
        if (RopeSpearHooks.ObjectType != null && data.itemType == RopeSpearHooks.ObjectType)
        {
            return DryCycleTokenRuntime.RopeSpearUnlock;
        }

        return orig(data);
    }

    private static void SandboxGameSession_SpawnItems(
        On.SandboxGameSession.orig_SpawnItems orig,
        SandboxGameSession self,
        IconSymbol.IconSymbolData data,
        WorldCoordinate pos,
        EntityID entityID)
    {
        if (RopeSpearHooks.ObjectType == null || data.itemType != RopeSpearHooks.ObjectType)
        {
            orig(self, data, pos, entityID);
            return;
        }

        if (self?.game?.world == null)
        {
            orig(self, data, pos, entityID);
            return;
        }

        // Vanilla SpawnItems creates a plain AbstractPhysicalObject for unknown item
        // types. RopeSpear needs its AbstractRopeSpear state container, otherwise it
        // cannot realize, serialize rope length, or create its handle after throwing.
        AbstractRoom room = self.game.world.GetAbstractRoom(0);
        if (room == null)
        {
            orig(self, data, pos, entityID);
            return;
        }

        room.AddEntity(new AbstractRopeSpear(self.game.world, pos, entityID));
    }

    private static string ItemSymbol_SpriteNameForItem(
        On.ItemSymbol.orig_SpriteNameForItem orig,
        AbstractPhysicalObject.AbstractObjectType itemType,
        int intData)
    {
        return RopeSpearHooks.ObjectType != null && itemType == RopeSpearHooks.ObjectType
            ? "Symbol_Spear"
            : orig(itemType, intData);
    }

    private static Color ItemSymbol_ColorForItem(
        On.ItemSymbol.orig_ColorForItem orig,
        AbstractPhysicalObject.AbstractObjectType itemType,
        int intData)
    {
        return RopeSpearHooks.ObjectType != null && itemType == RopeSpearHooks.ObjectType
            ? SandboxIconColor
            : orig(itemType, intData);
    }
}
