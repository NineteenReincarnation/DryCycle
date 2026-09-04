using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal static class RopeSpearHooks
{
    private const string ObjectTypeName = "RopeSpear";
    private const string HandleObjectTypeName = "RopeSpearHandle";
    private const string LengthPrefix = "DRYCYCLE_ROPESPEAR_LENGTH=";
    private const string BrokenPrefix = "DRYCYCLE_ROPESPEAR_BROKEN=";
    private const float RopeGrabRange = 27f;

    private sealed class PlayerRopeGrabState
    {
        internal RopeSpear Spear;
        internal float NormalizedPosition;
    }

    private static readonly ConditionalWeakTable<Player, PlayerRopeGrabState> RopeGrabStates = new();
    private static bool _enabled;

    public static AbstractPhysicalObject.AbstractObjectType ObjectType { get; private set; }

    public static AbstractPhysicalObject.AbstractObjectType HandleObjectType { get; private set; }

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        ObjectType = new AbstractPhysicalObject.AbstractObjectType(ObjectTypeName, register: true);
        HandleObjectType = new AbstractPhysicalObject.AbstractObjectType(HandleObjectTypeName, register: true);

        On.AbstractPhysicalObject.Realize += AbstractPhysicalObject_Realize;
        On.AbstractSpear.StuckInWallTick += AbstractSpear_StuckInWallTick;
        On.SaveState.AbstractPhysicalObjectFromString += SaveState_AbstractPhysicalObjectFromString;
        On.Player.Grabability += Player_Grabability;
        On.Player.GrabUpdate += Player_GrabUpdate;
        On.Player.ThrowObject += Player_ThrowObject;

        RopeSpearDevConsoleSupport.TryRegister();
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.AbstractPhysicalObject.Realize -= AbstractPhysicalObject_Realize;
        On.AbstractSpear.StuckInWallTick -= AbstractSpear_StuckInWallTick;
        On.SaveState.AbstractPhysicalObjectFromString -= SaveState_AbstractPhysicalObjectFromString;
        On.Player.Grabability -= Player_Grabability;
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.Player.ThrowObject -= Player_ThrowObject;

        RopeSpearDevConsoleSupport.ResetRegistration();

        HandleObjectType?.Unregister();
        HandleObjectType = null;
        ObjectType?.Unregister();
        ObjectType = null;
        _enabled = false;
    }

    private static void AbstractPhysicalObject_Realize(
        On.AbstractPhysicalObject.orig_Realize orig,
        AbstractPhysicalObject self)
    {
        orig(self);

        if (self.realizedObject != null)
        {
            return;
        }

        if (self is AbstractRopeSpear && self.type == ObjectType)
        {
            self.realizedObject = new RopeSpear(self, self.world);
        }
        else if (self is AbstractRopeHandle && self.type == HandleObjectType)
        {
            self.realizedObject = new RopeHandle(self);
        }
    }

    private static void AbstractSpear_StuckInWallTick(
        On.AbstractSpear.orig_StuckInWallTick orig,
        AbstractSpear self,
        int ticks)
    {
        if (self is AbstractRopeSpear ropeSpear && ropeSpear.HasPersistentHandleAnchor)
        {
            return;
        }

        orig(self, ticks);
    }

    private static Player.ObjectGrabability Player_Grabability(
        On.Player.orig_Grabability orig,
        Player self,
        PhysicalObject obj)
    {
        if (obj is RopeHandle)
        {
            return Player.ObjectGrabability.OneHand;
        }

        return orig(self, obj);
    }

    private static void Player_ThrowObject(
        On.Player.orig_ThrowObject orig,
        Player self,
        int grasp,
        bool eu)
    {
        bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (altHeld &&
            self?.grasps != null &&
            grasp >= 0 &&
            grasp < self.grasps.Length &&
            self.grasps[grasp]?.grabbed is RopeHandle handle &&
            handle.TryAnchorToNearbyTerrain())
        {
            self.ReleaseObject(grasp, eu);
            return;
        }

        orig(self, grasp, eu);
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        if (self == null)
        {
            orig(self, eu);
            return;
        }

        PlayerRopeGrabState state = RopeGrabStates.GetOrCreateValue(self);
        bool pickupPressed = self.input != null &&
                             self.input.Length > 1 &&
                             self.input[0].pckp &&
                             !self.input[1].pckp;

        if (state.Spear != null)
        {
            if (pickupPressed)
            {
                state.Spear = null;
                self.wantToPickUp = 0;
                orig(self, eu);
                return;
            }

            self.wantToPickUp = 0;
            orig(self, eu);

            if (!state.Spear.UpdatePlayerRopeGrab(self, ref state.NormalizedPosition))
            {
                state.Spear = null;
            }
            return;
        }

        if (pickupPressed &&
            self.FreeHand() >= 0 &&
            TryFindNearestRope(self, out RopeSpear spear, out float normalizedPosition))
        {
            state.Spear = spear;
            state.NormalizedPosition = normalizedPosition;
            self.wantToPickUp = 0;
            orig(self, eu);

            if (!state.Spear.UpdatePlayerRopeGrab(self, ref state.NormalizedPosition))
            {
                state.Spear = null;
            }
            return;
        }

        orig(self, eu);
    }

    private static bool TryFindNearestRope(
        Player player,
        out RopeSpear bestSpear,
        out float normalizedPosition)
    {
        bestSpear = null;
        normalizedPosition = 0f;
        if (player?.room?.physicalObjects == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        Vector2 position = player.mainBodyChunk.pos;

        for (int layer = 0; layer < player.room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = player.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not RopeSpear spear ||
                    !spear.TryFindNearestRopePoint(
                        position,
                        RopeGrabRange,
                        out float candidatePosition,
                        out float distance) ||
                    distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                bestSpear = spear;
                normalizedPosition = candidatePosition;
            }
        }

        return bestSpear != null;
    }

    private static AbstractPhysicalObject SaveState_AbstractPhysicalObjectFromString(
        On.SaveState.orig_AbstractPhysicalObjectFromString orig,
        World world,
        string objString)
    {
        string[] parts = Regex.Split(objString ?? string.Empty, "<oA>");
        if (parts.Length < 3)
        {
            return orig(world, objString);
        }

        if (parts[1] == ObjectTypeName)
        {
            return ParseRopeSpear(orig, world, objString, parts);
        }

        if (parts[1] == HandleObjectTypeName)
        {
            return ParseRopeHandle(orig, world, objString, parts);
        }

        return orig(world, objString);
    }

    private static AbstractPhysicalObject ParseRopeSpear(
        On.SaveState.orig_AbstractPhysicalObjectFromString orig,
        World world,
        string objString,
        string[] parts)
    {
        try
        {
            ParseIDAndRipple(parts[0], out EntityID id, out int rippleLayer);
            WorldCoordinate pos = WorldCoordinate.FromString(parts[2]);
            AbstractRopeSpear result = new(
                world,
                pos,
                id,
                AbstractRopeSpear.DefaultRopeLength,
                ropeBroken: false);

            if (parts.Length > 3)
            {
                result.stuckInWallCycles = int.Parse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture);
            }
            if (parts.Length > 4)
            {
                result.explosive = parts[4] == "1";
            }

            int fromIndex = 5;
            if (ModManager.DLCShared && parts.Length >= 9)
            {
                result.hue = float.Parse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.electric = parts[6] == "1";
                result.electricCharge = int.Parse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.needle = parts[8] == "1";
                fromIndex = 9;
            }

            if (ModManager.Watcher && parts.Length >= 11)
            {
                result.poison = float.Parse(parts[9], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.poisonHue = float.Parse(parts[10], NumberStyles.Any, CultureInfo.InvariantCulture);
                fromIndex = 11;
            }

            List<string> unrecognized = new();
            for (int i = fromIndex; i < parts.Length; i++)
            {
                string attr = parts[i];
                if (attr.StartsWith(LengthPrefix, StringComparison.Ordinal) &&
                    float.TryParse(
                        attr.Substring(LengthPrefix.Length),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out float length))
                {
                    result.RopeLength = Mathf.Clamp(
                        length,
                        AbstractRopeSpear.MinRopeLength,
                        AbstractRopeSpear.MaxRopeLength);
                }
                else if (attr.StartsWith(BrokenPrefix, StringComparison.Ordinal))
                {
                    string value = attr.Substring(BrokenPrefix.Length);
                    result.RopeBroken = value == "1" ||
                                        bool.TryParse(value, out bool parsed) && parsed;
                }
                else if (attr.StartsWith(AbstractRopeSpear.FixedHandlePrefix, StringComparison.Ordinal))
                {
                    string value = attr.Substring(AbstractRopeSpear.FixedHandlePrefix.Length);
                    result.HasPersistentHandleAnchor = value == "1" ||
                                                       bool.TryParse(value, out bool fixedParsed) && fixedParsed;
                }
                else if (attr.StartsWith(AbstractRopeSpear.FixedHandleAnchorPrefix, StringComparison.Ordinal) &&
                         TryParseVector2(
                             attr.Substring(AbstractRopeSpear.FixedHandleAnchorPrefix.Length),
                             out Vector2 fixedAnchor))
                {
                    result.PersistentHandleAnchor = fixedAnchor;
                }
                else if (!string.IsNullOrEmpty(attr))
                {
                    unrecognized.Add(attr);
                }
            }

            result.unrecognizedAttributes = unrecognized.Count > 0
                ? unrecognized.ToArray()
                : null;
            result.rippleLayer = rippleLayer;
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to deserialize RopeSpear: {ex}");
            return orig(world, objString);
        }
    }

    private static AbstractPhysicalObject ParseRopeHandle(
        On.SaveState.orig_AbstractPhysicalObjectFromString orig,
        World world,
        string objString,
        string[] parts)
    {
        try
        {
            ParseIDAndRipple(parts[0], out EntityID id, out int rippleLayer);
            WorldCoordinate pos = WorldCoordinate.FromString(parts[2]);
            EntityID parentID = default;
            bool hasParent = false;
            bool anchored = false;
            Vector2 anchorPosition = Vector2.zero;
            List<string> unrecognized = new();

            for (int i = 3; i < parts.Length; i++)
            {
                string attr = parts[i];
                if (attr.StartsWith(AbstractRopeHandle.ParentPrefix, StringComparison.Ordinal))
                {
                    parentID = EntityID.FromString(attr.Substring(AbstractRopeHandle.ParentPrefix.Length));
                    hasParent = true;
                }
                else if (attr.StartsWith(AbstractRopeHandle.AnchoredPrefix, StringComparison.Ordinal))
                {
                    string value = attr.Substring(AbstractRopeHandle.AnchoredPrefix.Length);
                    anchored = value == "1" || bool.TryParse(value, out bool parsed) && parsed;
                }
                else if (attr.StartsWith(AbstractRopeHandle.AnchorPrefix, StringComparison.Ordinal) &&
                         TryParseVector2(
                             attr.Substring(AbstractRopeHandle.AnchorPrefix.Length),
                             out Vector2 parsedAnchor))
                {
                    anchorPosition = parsedAnchor;
                }
                else if (!string.IsNullOrEmpty(attr))
                {
                    unrecognized.Add(attr);
                }
            }

            if (!hasParent)
            {
                throw new FormatException("RopeSpearHandle save data has no parent spear ID.");
            }

            AbstractRopeHandle result = new(
                world,
                pos,
                id,
                parentID,
                anchored,
                anchorPosition)
            {
                rippleLayer = rippleLayer,
                unrecognizedAttributes = unrecognized.Count > 0
                    ? unrecognized.ToArray()
                    : null
            };
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to deserialize RopeSpearHandle: {ex}");
            return orig(world, objString);
        }
    }

    private static void ParseIDAndRipple(
        string value,
        out EntityID id,
        out int rippleLayer)
    {
        rippleLayer = 0;
        if (value.Contains("<oB>"))
        {
            string[] idParts = Regex.Split(value, "<oB>");
            id = EntityID.FromString(idParts[0]);
            rippleLayer = int.Parse(idParts[1], NumberStyles.Any, CultureInfo.InvariantCulture);
        }
        else
        {
            id = EntityID.FromString(value);
        }
    }

    private static bool TryParseVector2(string value, out Vector2 parsed)
    {
        parsed = Vector2.zero;
        string[] pieces = value.Split(',');
        if (pieces.Length != 2 ||
            !float.TryParse(pieces[0], NumberStyles.Any, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(pieces[1], NumberStyles.Any, CultureInfo.InvariantCulture, out float y))
        {
            return false;
        }

        parsed = new Vector2(x, y);
        return true;
    }
}
