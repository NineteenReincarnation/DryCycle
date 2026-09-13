using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DryCycle.Framework.KarmicManipulation;
using UnityEngine;
using Watcher;

namespace DryCycle.Items.KarmaSpear;

internal static class KarmaSpearHooks
{
    private const string ObjectTypeName = "KarmaSpear";
    private const string KarmaLevelPrefix = "DRYCYCLE_KARMA_LEVEL=";
    private const string SpentPrefix = "DRYCYCLE_KARMA_SPENT=";
    private const int ChargeFramesRequired = 30;

    private static ConditionalWeakTable<Player, TransmutationState> _states = new();
    private static bool _enabled;

    internal static AbstractPhysicalObject.AbstractObjectType ObjectType { get; private set; }

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        ObjectType = new AbstractPhysicalObject.AbstractObjectType(ObjectTypeName, register: true);
        On.AbstractPhysicalObject.Realize += AbstractPhysicalObject_Realize;
        On.SaveState.AbstractPhysicalObjectFromString += SaveState_AbstractPhysicalObjectFromString;
        On.Player.GrabUpdate += Player_GrabUpdate;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.AbstractPhysicalObject.Realize -= AbstractPhysicalObject_Realize;
        On.SaveState.AbstractPhysicalObjectFromString -= SaveState_AbstractPhysicalObjectFromString;
        On.Player.GrabUpdate -= Player_GrabUpdate;

        ObjectType?.Unregister();
        ObjectType = null;
        _states = new ConditionalWeakTable<Player, TransmutationState>();
    }

    private static void AbstractPhysicalObject_Realize(
        On.AbstractPhysicalObject.orig_Realize orig,
        AbstractPhysicalObject self)
    {
        orig(self);

        if (self is AbstractKarmaSpear &&
            self.type == ObjectType &&
            self.realizedObject == null)
        {
            self.realizedObject = new KarmaSpear(self, self.world);
        }
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        TransmutationState state = _states.GetOrCreateValue(self);
        bool pickupHeld = self.input != null && self.input.Length > 0 && self.input[0].pckp;

        if (!pickupHeld)
        {
            state.Reset(clearReleaseLatch: true);
            orig(self, eu);
            return;
        }

        if (state.RequiresRelease)
        {
            self.wantToThrow = 0;
            self.wantToPickUp = 0;
            return;
        }

        if (!CanTransmute(self, out Spear spear, out int hand))
        {
            state.Reset(clearReleaseLatch: false);
            orig(self, eu);
            return;
        }

        if (state.Source != spear || state.Hand != hand)
        {
            state.Source = spear;
            state.Hand = hand;
            state.Progress = 0;
        }

        // Hold pickup to channel reinforced karma into either an ordinary spear or a
        // depleted Karma Spear. The level stored in the finished weapon is read from the
        // player's current karma at the moment reinforced karma is consumed.
        self.wantToThrow = 0;
        self.wantToPickUp = 0;
        state.Progress++;

        if (state.Progress == 1)
        {
            self.room?.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Tick_1, spear.firstChunk);
        }

        float progress01 = Mathf.Clamp01((float)state.Progress / ChargeFramesRequired);
        if (state.Progress % 5 == 0)
        {
            int karmaLevel = KarmicManipulationRuntime.CurrentKarmaLevel(self);
            KarmicVisualEffects.SpawnChargePulse(self, progress01 * 0.8f);
            KarmicVisualEffects.SpawnChargingConvergence(self, spear, karmaLevel, progress01);
        }

        if (state.Progress % 10 == 0)
        {
            KarmicVisualEffects.SpawnChargePulse(spear, progress01);
        }

        if (state.Progress < ChargeFramesRequired)
        {
            return;
        }

        if (TryTransmuteHeldSpear(
                self,
                spear,
                hand,
                out KarmaSpear karmaSpear,
                out KarmicCharge charge))
        {
            KarmicVisualEffects.SpawnTransfer(self, karmaSpear, charge.KarmaLevel);
            self.room?.PlaySound(WatcherEnums.WatcherSoundID.Templar_Shield_Tick_9, karmaSpear.firstChunk);
        }

        state.RequiresRelease = true;
        state.Source = null;
        state.Hand = -1;
        state.Progress = 0;
    }

    private static bool CanTransmute(Player player, out Spear spear, out int hand)
    {
        spear = null;
        hand = -1;

        if (player?.room == null ||
            player.dead ||
            !player.Consious ||
            player.isNPC ||
            player.inShortcut ||
            !KarmicManipulationRuntime.HasProtection(player))
        {
            return false;
        }

        for (int i = 0; i < player.grasps.Length; i++)
        {
            if (player.grasps[i]?.grabbed is not Spear candidate)
            {
                continue;
            }

            // Only a depleted Karma Spear accepts a new reinforced charge. When it does,
            // Recharge replaces the stored value with the player's current karma level.
            if (candidate is KarmaSpear karmaCandidate)
            {
                if (karmaCandidate.IsSpent &&
                    karmaCandidate.abstractPhysicalObject is AbstractKarmaSpear abstractKarma &&
                    (abstractKarma.Spent || abstractKarma.KarmaLevel <= 0))
                {
                    spear = karmaCandidate;
                    hand = i;
                    return true;
                }

                continue;
            }

            if (candidate.GetType() != typeof(Spear) ||
                candidate.abstractPhysicalObject is not AbstractSpear abstractSpear)
            {
                continue;
            }

            if (abstractSpear.explosive ||
                abstractSpear.electric ||
                abstractSpear.needle ||
                abstractSpear.poison > 0f ||
                Mathf.Abs(abstractSpear.hue) > 0.0001f)
            {
                continue;
            }

            spear = candidate;
            hand = i;
            return true;
        }

        return false;
    }

    private static bool TryTransmuteHeldSpear(
        Player player,
        Spear source,
        int hand,
        out KarmaSpear karmaSpear,
        out KarmicCharge charge)
    {
        karmaSpear = null;
        charge = default;

        if (player?.room == null ||
            source?.room != player.room ||
            hand < 0 ||
            hand >= player.grasps.Length ||
            player.grasps[hand]?.grabbed != source ||
            !KarmicManipulationRuntime.HasProtection(player))
        {
            return false;
        }

        if (source is KarmaSpear spentKarmaSpear)
        {
            if (!spentKarmaSpear.IsSpent ||
                spentKarmaSpear.abstractPhysicalObject is not AbstractKarmaSpear spentAbstract ||
                (!spentAbstract.Spent && spentAbstract.KarmaLevel > 0))
            {
                return false;
            }

            if (!KarmicManipulationRuntime.TryConsumeProtection(
                    player,
                    KarmicManipulationUse.KarmaSpear,
                    out charge))
            {
                return false;
            }

            spentKarmaSpear.Recharge(charge.KarmaLevel);
            karmaSpear = spentKarmaSpear;
            return true;
        }

        Room room = player.room;
        Vector2 position = source.firstChunk.pos;
        Vector2 velocity = source.firstChunk.vel;
        Vector2 spearRotation = source.rotation.sqrMagnitude > 0.001f
            ? source.rotation.normalized
            : Vector2.right;

        int previewKarmaLevel = KarmicManipulationRuntime.CurrentKarmaLevel(player);
        AbstractKarmaSpear abstractSpear = new(
            room.world,
            room.GetWorldCoordinate(position),
            room.game.GetNewID(),
            previewKarmaLevel,
            spent: false);

        room.abstractRoom.AddEntity(abstractSpear);
        abstractSpear.RealizeInRoom();

        if (abstractSpear.realizedObject is not KarmaSpear realized)
        {
            room.abstractRoom.RemoveEntity(abstractSpear);
            return false;
        }

        if (!KarmicManipulationRuntime.TryConsumeProtection(
                player,
                KarmicManipulationUse.KarmaSpear,
                out charge))
        {
            realized.Destroy();
            abstractSpear.realizedObject = null;
            room.abstractRoom.RemoveEntity(abstractSpear);
            return false;
        }

        // This is the actual stored-energy assignment. It intentionally copies the player's
        // current karma level once; subsequent spear uses only decrement this stored value.
        abstractSpear.KarmaLevel = charge.KarmaLevel;
        abstractSpear.Spent = abstractSpear.KarmaLevel <= 0;
        realized.firstChunk.HardSetPosition(position);
        realized.firstChunk.lastPos = position;
        realized.firstChunk.vel = velocity;
        realized.rotation = spearRotation;
        realized.lastRotation = spearRotation;
        realized.setRotation = spearRotation;

        player.ReleaseGrasp(hand);
        source.abstractPhysicalObject?.LoseAllStuckObjects();
        if (source.abstractPhysicalObject != null)
        {
            room.abstractRoom.RemoveEntity(source.abstractPhysicalObject);
            source.abstractPhysicalObject.realizedObject = null;
        }
        source.Destroy();

        player.SlugcatGrab(realized, hand);
        karmaSpear = realized;
        return true;
    }

    private static AbstractPhysicalObject SaveState_AbstractPhysicalObjectFromString(
        On.SaveState.orig_AbstractPhysicalObjectFromString orig,
        World world,
        string objString)
    {
        string[] parts = Regex.Split(objString ?? string.Empty, "<oA>");
        if (parts.Length < 5 || parts[1] != ObjectTypeName)
        {
            return orig(world, objString);
        }

        try
        {
            int rippleLayer = 0;
            EntityID id;
            if (parts[0].Contains("<oB>"))
            {
                string[] idParts = Regex.Split(parts[0], "<oB>");
                id = EntityID.FromString(idParts[0]);
                rippleLayer = int.Parse(idParts[1], NumberStyles.Any, CultureInfo.InvariantCulture);
            }
            else
            {
                id = EntityID.FromString(parts[0]);
            }

            WorldCoordinate pos = WorldCoordinate.FromString(parts[2]);
            AbstractKarmaSpear result = new(world, pos, id, karmaLevel: 1, spent: false)
            {
                stuckInWallCycles = int.Parse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture),
                explosive = parts[4] == "1"
            };

            int fromIndex = 5;
            if (ModManager.DLCShared && parts.Length > 8)
            {
                result.hue = float.Parse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.electric = parts[6] == "1";
                result.electricCharge = int.Parse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.needle = parts[8] == "1";
                fromIndex = 9;
            }

            if (ModManager.Watcher && parts.Length > 10)
            {
                result.poison = float.Parse(parts[9], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.poisonHue = float.Parse(parts[10], NumberStyles.Any, CultureInfo.InvariantCulture);
                fromIndex = 11;
            }

            List<string> unrecognized = new();
            for (int i = fromIndex; i < parts.Length; i++)
            {
                string attr = parts[i];
                if (attr.StartsWith(KarmaLevelPrefix, StringComparison.Ordinal) &&
                    int.TryParse(
                        attr.Substring(KarmaLevelPrefix.Length),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int level))
                {
                    result.KarmaLevel = Mathf.Clamp(level, 0, 10);
                }
                else if (attr.StartsWith(SpentPrefix, StringComparison.Ordinal) &&
                         int.TryParse(
                             attr.Substring(SpentPrefix.Length),
                             NumberStyles.Integer,
                             CultureInfo.InvariantCulture,
                             out int spent))
                {
                    result.Spent = spent != 0;
                }
                else if (!string.IsNullOrEmpty(attr))
                {
                    unrecognized.Add(attr);
                }
            }

            // Normalize old saves and malformed combinations. Zero always means depleted;
            // an old `spent=true` spear is migrated to the new zero-level representation.
            if (result.Spent || result.KarmaLevel <= 0)
            {
                result.KarmaLevel = 0;
                result.Spent = true;
            }
            else
            {
                result.KarmaLevel = Mathf.Clamp(result.KarmaLevel, 1, 10);
                result.Spent = false;
            }

            result.unrecognizedAttributes = unrecognized.Count > 0
                ? unrecognized.ToArray()
                : null;
            result.rippleLayer = rippleLayer;
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to deserialize KarmaSpear: {ex}");
            return orig(world, objString);
        }
    }

    private sealed class TransmutationState
    {
        internal Spear Source;
        internal int Hand = -1;
        internal int Progress;
        internal bool RequiresRelease;

        internal void Reset(bool clearReleaseLatch)
        {
            Source = null;
            Hand = -1;
            Progress = 0;
            if (clearReleaseLatch)
            {
                RequiresRelease = false;
            }
        }
    }
}
