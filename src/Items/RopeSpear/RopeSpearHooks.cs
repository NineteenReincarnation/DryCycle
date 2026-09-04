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

    // While the spear is actually flying, the rope is allowed to pay out freely.
    // The extra slack keeps the Verlet chain and vanilla corner topology from
    // producing a transient pull when the projectile crosses a tile edge.
    private const float FlightPayoutSlack = 220f;
    private const float SettledPayoutSlack = 24f;
    private const float SpearEndExitDistance = 38f;

    private sealed class FlightState
    {
        internal bool InFlight;
        internal float PreThrowLength;
    }

    private static readonly ConditionalWeakTable<RopeSpear, FlightState> FlightStates = new();

    // ClimbableVinesSystem.VineOverlap has no Creature parameter. Player.Update is
    // the caller for normal slugcat vine acquisition, so keep the active player in
    // a short-lived context while vanilla movement runs. This lets RopeSpear require
    // an explicit pickup press instead of stealing Up input at ledges.
    private static Player _playerUpdating;
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
        On.Player.ThrowObject += Player_ThrowObject;
        On.Player.Update += Player_Update;
        On.ClimbableVinesSystem.VineOverlap += ClimbableVinesSystem_VineOverlap;
        On.Spear.Update += Spear_Update;

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
        On.Player.ThrowObject -= Player_ThrowObject;
        On.Player.Update -= Player_Update;
        On.ClimbableVinesSystem.VineOverlap -= ClimbableVinesSystem_VineOverlap;
        On.Spear.Update -= Spear_Update;

        _playerUpdating = null;
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
        // A rope with both ends deliberately fixed is a player-built room feature,
        // not a temporary vanilla wall spear. Keep it in RegionState until the
        // player removes one of the anchors or picks the RopeSpear back up.
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

    private static void Player_Update(
        On.Player.orig_Update orig,
        Player self,
        bool eu)
    {
        Player previous = _playerUpdating;
        _playerUpdating = self;
        try
        {
            orig(self, eu);
        }
        finally
        {
            _playerUpdating = previous;
        }

        TryExitRopeAtSpear(self);
    }

    private static ClimbableVinesSystem.VinePosition ClimbableVinesSystem_VineOverlap(
        On.ClimbableVinesSystem.orig_VineOverlap orig,
        ClimbableVinesSystem self,
        Vector2 pos,
        float rad)
    {
        ClimbableVinesSystem.VinePosition result = orig(self, pos, rad);
        if (result?.vine is not RopeSpear || _playerUpdating == null)
        {
            return result;
        }

        bool alreadyHoldingThisRope =
            _playerUpdating.animation == Player.AnimationIndex.VineGrab &&
            _playerUpdating.vinePos?.vine == result.vine;
        if (alreadyHoldingThisRope)
        {
            return result;
        }

        // Vanilla vines can be acquired merely by pressing Up. That is useful for
        // plants, but a player-built rope often lies against a ledge where Up is
        // also used for movement. RopeSpear therefore requires the pickup button
        // to be held for the initial grab. Once grabbed, vanilla climbing remains
        // fully in control and no further pickup input is required.
        bool explicitPickup = _playerUpdating.input != null &&
                              _playerUpdating.input.Length > 0 &&
                              _playerUpdating.input[0].pckp;
        return explicitPickup ? result : null;
    }

    private static void Spear_Update(
        On.Spear.orig_Update orig,
        Spear self,
        bool eu)
    {
        if (self is not RopeSpear rope ||
            rope.abstractPhysicalObject is not AbstractRopeSpear data)
        {
            orig(self, eu);
            return;
        }

        FlightState state = FlightStates.GetOrCreateValue(rope);
        bool thrownOnEntry = rope.mode == Weapon.Mode.Thrown;
        if (thrownOnEntry && !state.InFlight)
        {
            state.InFlight = true;
            state.PreThrowLength = data.RopeLength;
        }

        orig(self, eu);

        if (!state.InFlight)
        {
            return;
        }

        if (rope.mode == Weapon.Mode.Thrown)
        {
            // Pay rope out to the projectile instead of using rope tension as a
            // projectile range limiter. This is deliberately written directly to
            // AbstractRopeSpear so a previously shortened rope cannot pull a fresh
            // throw back toward the player.
            float span = MeasureHandleToSpearSpan(rope);
            data.RopeLength = Mathf.Max(state.PreThrowLength, span + FlightPayoutSlack);
            return;
        }

        // Picking the spear back up is an explicit full reset handled by
        // RopeSpear.PickedUp; do not undo that reset with the flight settlement.
        if (rope.mode == Weapon.Mode.Carried)
        {
            state.InFlight = false;
            return;
        }

        // Once the projectile has stuck or otherwise left Thrown mode, keep only
        // the amount of rope actually paid out plus a small neutral slack. From
        // this point Alt+Up/Down and normal rope tension take over again.
        float settledSpan = MeasureHandleToSpearSpan(rope);
        data.RopeLength = Mathf.Max(state.PreThrowLength, settledSpan + SettledPayoutSlack);
        state.InFlight = false;
    }

    private static float MeasureHandleToSpearSpan(RopeSpear rope)
    {
        if (rope?.room?.physicalObjects == null || rope.abstractPhysicalObject == null)
        {
            return 0f;
        }

        EntityID spearID = rope.abstractPhysicalObject.ID;
        for (int layer = 0; layer < rope.room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = rope.room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is RopeHandle handle &&
                    !handle.slatedForDeletetion &&
                    handle.ParentSpearID == spearID)
                {
                    return Vector2.Distance(handle.firstChunk.pos, rope.firstChunk.pos);
                }
            }
        }

        return 0f;
    }

    private static void TryExitRopeAtSpear(Player player)
    {
        if (player?.room?.climbableVines == null ||
            player.animation != Player.AnimationIndex.VineGrab ||
            player.vinePos?.vine is not RopeSpear rope ||
            player.input == null ||
            player.input.Length == 0 ||
            player.input[0].y <= 0 ||
            rope.mode != Weapon.Mode.StuckInWall)
        {
            return;
        }

        float totalLength = player.room.climbableVines.TotalLength(rope);
        float remaining = Mathf.Max(0f, 1f - player.vinePos.floatPos) * totalLength;
        if (remaining > SpearEndExitDistance)
        {
            return;
        }

        int last = rope.TotalPositions() - 1;
        Vector2 spearEnd = rope.Pos(last);
        if (spearEnd.y + 5f < player.mainBodyChunk.pos.y ||
            !Custom.DistLess(player.mainBodyChunk.pos, spearEnd, 48f))
        {
            return;
        }

        player.vineGrabDelay = 15;
        player.noGrabCounter = Mathf.Max(player.noGrabCounter, 15);

        if (TryFindHorizontalSpearBeam(player.room, rope.firstChunk.pos, player.mainBodyChunk.pos, out Vector2 beamCenter))
        {
            // Hand control from VineGrab directly to vanilla's horizontal-beam
            // pull-up animation. This avoids the stock vine endpoint rule
            // (floatPos==1 -> speed -1) that otherwise sends the player straight
            // back down after reaching the spear.
            int outsideDir = rope.rotation.x >= 0f ? -1 : 1;
            player.flipDirection = outsideDir;
            player.pullupSoftlockSafety = 0;
            player.straightUpOnHorizontalBeam = true;
            player.forceFeetToHorizontalBeamTile = 20;
            player.upOnHorizontalBeamPos = beamCenter + new Vector2(0f, 20f);
            player.animation = Player.AnimationIndex.GetUpOnBeam;
            player.bodyMode = Player.BodyModeIndex.ClimbingOnBeam;
            player.standing = false;

            player.mainBodyChunk.pos = beamCenter;
            player.mainBodyChunk.lastPos = beamCenter;
            player.mainBodyChunk.vel = Vector2.zero;

            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                Vector2 lower = beamCenter + new Vector2(0f, -17f);
                player.bodyChunks[1].pos = lower;
                player.bodyChunks[1].lastPos = lower;
                player.bodyChunks[1].vel = Vector2.zero;
            }

            return;
        }

        // Fallback for unusual spear states where the vanilla horizontal-beam tile
        // has not been installed yet: release upward instead of allowing the vine
        // endpoint to reverse the player's climb direction.
        player.animation = Player.AnimationIndex.None;
        player.bodyMode = Player.BodyModeIndex.Default;
        player.mainBodyChunk.vel.y = Mathf.Max(player.mainBodyChunk.vel.y, 5.5f);
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel.y = Mathf.Max(player.bodyChunks[1].vel.y, 4.2f);
        }
    }

    private static bool TryFindHorizontalSpearBeam(
        Room room,
        Vector2 spearPosition,
        Vector2 playerPosition,
        out Vector2 beamCenter)
    {
        beamCenter = Vector2.zero;
        if (room == null)
        {
            return false;
        }

        IntVector2 origin = room.GetTilePosition(spearPosition);
        float bestDistance = float.MaxValue;
        bool found = false;

        for (int x = -2; x <= 2; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                IntVector2 tilePos = origin + new IntVector2(x, y);
                Room.Tile tile = room.GetTile(tilePos);
                if (!tile.horizontalBeam || tile.Solid)
                {
                    continue;
                }

                Vector2 center = room.MiddleOfTile(tilePos);
                float distance = Vector2.Distance(playerPosition, center);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                beamCenter = center;
                found = true;
            }
        }

        return found;
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
