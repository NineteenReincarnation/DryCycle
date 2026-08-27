using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.KingVultureSpear;

internal static class KingVultureSpearHooks
{
    private const string ObjectTypeName = "KingVultureSpear";
    private const int PullFramesRequired = 55;
    private const float PullRange = 70f;
    private const float MaxTuskDistanceFromHead = 120f;

    private const string SidePrefix = "DRYCYCLE_KVS_SIDE=";
    private const string ArmorPrefix = "DRYCYCLE_KVS_ARMOR=";
    private const string ColorAPrefix = "DRYCYCLE_KVS_A=";
    private const string ColorBPrefix = "DRYCYCLE_KVS_B=";
    private const string PatternPrefix = "DRYCYCLE_KVS_PATTERN=";
    private const string ProfilePrefix = "DRYCYCLE_KVS_PROFILE=";

    private sealed class VultureExtractionState
    {
        public readonly bool[] Extracted = new bool[2];
    }

    private sealed class PlayerPullState
    {
        public AbstractCreature Target;
        public int Side = -1;
        public int Progress;
        public bool RequiresRelease;
    }

    private readonly struct TuskCandidate
    {
        public TuskCandidate(Vulture vulture, KingTusks.Tusk tusk, int side, float distance)
        {
            Vulture = vulture;
            Tusk = tusk;
            Side = side;
            Distance = distance;
        }

        public Vulture Vulture { get; }
        public KingTusks.Tusk Tusk { get; }
        public int Side { get; }
        public float Distance { get; }
    }

    private static readonly ConditionalWeakTable<AbstractCreature, VultureExtractionState> ExtractionStates = new();
    private static readonly ConditionalWeakTable<Player, PlayerPullState> PullStates = new();

    private static bool _enabled;

    public static AbstractPhysicalObject.AbstractObjectType ObjectType { get; private set; }

    public static void Enable()
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
        On.KingTusks.Tusk.DrawSprites += Tusk_DrawSprites;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;

        On.AbstractPhysicalObject.Realize -= AbstractPhysicalObject_Realize;
        On.SaveState.AbstractPhysicalObjectFromString -= SaveState_AbstractPhysicalObjectFromString;
        On.Player.GrabUpdate -= Player_GrabUpdate;
        On.KingTusks.Tusk.DrawSprites -= Tusk_DrawSprites;

        ObjectType?.Unregister();
        ObjectType = null;
    }

    private static void AbstractPhysicalObject_Realize(
        On.AbstractPhysicalObject.orig_Realize orig,
        AbstractPhysicalObject self)
    {
        orig(self);

        if (self != null &&
            self.type == ObjectType &&
            self.realizedObject == null)
        {
            self.realizedObject = new KingVultureSpear(self, self.world);
        }
    }

    private static void Player_GrabUpdate(
        On.Player.orig_GrabUpdate orig,
        Player self,
        bool eu)
    {
        if (TryHandleExtraction(self))
        {
            return;
        }

        orig(self, eu);
    }

    private static bool TryHandleExtraction(Player player)
    {
        if (player == null)
        {
            return false;
        }

        PlayerPullState pull = PullStates.GetOrCreateValue(player);
        bool pickupHeld = player.input != null &&
                          player.input.Length > 0 &&
                          player.input[0].pckp;

        if (!pickupHeld)
        {
            ResetPull(pull, clearReleaseLatch: true);
            return false;
        }

        if (pull.RequiresRelease)
        {
            return true;
        }

        if (player.room == null ||
            player.dead ||
            !player.Consious ||
            player.isNPC ||
            player.inShortcut ||
            player.FreeHand() < 0)
        {
            ResetPull(pull, clearReleaseLatch: false);
            return false;
        }

        if (!FindNearestTusk(player, out TuskCandidate candidate))
        {
            ResetPull(pull, clearReleaseLatch: false);
            return false;
        }

        if (pull.Target != candidate.Vulture.abstractCreature || pull.Side != candidate.Side)
        {
            pull.Target = candidate.Vulture.abstractCreature;
            pull.Side = candidate.Side;
            pull.Progress = 0;
        }

        pull.Progress++;
        ApplyPullFeedback(player, candidate.Vulture, candidate.Tusk);

        if (pull.Progress >= PullFramesRequired)
        {
            int freeHand = player.FreeHand();
            if (freeHand >= 0 && ExtractTusk(player, candidate, freeHand))
            {
                pull.Target = null;
                pull.Side = -1;
                pull.Progress = 0;
                pull.RequiresRelease = true;
            }
            else
            {
                ResetPull(pull, clearReleaseLatch: false);
            }
        }

        return true;
    }

    private static bool FindNearestTusk(Player player, out TuskCandidate candidate)
    {
        candidate = default;
        float bestDistance = float.MaxValue;
        bool found = false;

        if (player?.room?.abstractRoom?.creatures == null)
        {
            return false;
        }

        foreach (AbstractCreature abstractCreature in player.room.abstractRoom.creatures)
        {
            if (abstractCreature?.realizedCreature is not Vulture vulture ||
                !vulture.IsKing ||
                !vulture.dead ||
                vulture.kingTusks?.tusks == null ||
                vulture.kingTusks.tusks.Length < 2)
            {
                continue;
            }

            for (int side = 0; side < 2; side++)
            {
                if (IsExtracted(abstractCreature, side))
                {
                    continue;
                }

                KingTusks.Tusk tusk = vulture.kingTusks.tusks[side];
                if (!IsTuskStillAtHead(tusk))
                {
                    continue;
                }

                Vector2 center = GetTuskCenter(tusk);
                float distance = Mathf.Min(
                    Vector2.Distance(player.mainBodyChunk.pos, center),
                    Vector2.Distance(player.mainBodyChunk.pos, vulture.bodyChunks[4].pos));

                if (distance > PullRange || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                candidate = new TuskCandidate(vulture, tusk, side, distance);
                found = true;
            }
        }

        return found;
    }

    private static bool IsTuskStillAtHead(KingTusks.Tusk tusk)
    {
        if (tusk?.vulture == null ||
            tusk.chunkPoints == null ||
            tusk.attached < 0.65f)
        {
            return false;
        }

        return Vector2.Distance(GetTuskCenter(tusk), tusk.head.pos) <= MaxTuskDistanceFromHead;
    }

    private static void ApplyPullFeedback(Player player, Vulture vulture, KingTusks.Tusk tusk)
    {
        Vector2 center = GetTuskCenter(tusk);
        Vector2 pullDirection = Custom.DirVec(center, player.mainBodyChunk.pos);

        if (tusk.chunkPoints != null)
        {
            tusk.chunkPoints[0, 2] += pullDirection * 0.16f;
            tusk.chunkPoints[1, 2] += pullDirection * 0.10f;
        }

        if (vulture?.bodyChunks != null && vulture.bodyChunks.Length > 4)
        {
            vulture.bodyChunks[4].vel += pullDirection * 0.035f;
        }

        player.mainBodyChunk.vel -= pullDirection * 0.02f;
    }

    private static bool ExtractTusk(Player player, TuskCandidate candidate, int freeHand)
    {
        Vulture vulture = candidate.Vulture;
        KingTusks.Tusk tusk = candidate.Tusk;

        if (player?.room == null ||
            vulture?.abstractCreature == null ||
            IsExtracted(vulture.abstractCreature, candidate.Side) ||
            !IsTuskStillAtHead(tusk))
        {
            return false;
        }

        Vector2 center = GetTuskCenter(tusk);
        Vector2 direction = GetTuskDirection(tusk);
        Vector2 profile = tusk.zRot;
        if (profile.sqrMagnitude < 0.0001f)
        {
            profile = new Vector2(0.35f, 0.25f);
        }

        VultureGraphics graphics = vulture.graphicsModule as VultureGraphics;
        HSLColor colorA = graphics?.ColorA ?? new HSLColor(0f, 0.45f, 0.55f);
        HSLColor colorB = graphics?.ColorB ?? new HSLColor(0f, 0.85f, 0.45f);
        Color armor = graphics != null
            ? Color.Lerp(graphics.ColorA.rgb, Color.white, 0.35f)
            : tusk.armorColor;
        float pattern = vulture.kingTusks?.patternDisplace ?? 1f;

        AbstractKingVultureSpear abstractSpear = new(
            player.room.world,
            player.room.GetWorldCoordinate(center),
            player.room.game.GetNewID(),
            candidate.Side,
            armor,
            colorA,
            colorB,
            pattern,
            profile);

        player.room.abstractRoom.AddEntity(abstractSpear);
        abstractSpear.RealizeInRoom();

        if (abstractSpear.realizedObject is not KingVultureSpear spear)
        {
            player.room.abstractRoom.RemoveEntity(abstractSpear);
            return false;
        }

        spear.firstChunk.HardSetPosition(center);
        spear.firstChunk.lastPos = center;
        spear.firstChunk.vel = Vector2.zero;
        spear.rotation = direction;
        spear.lastRotation = direction;
        spear.setRotation = direction;

        ExtractionStates.GetOrCreateValue(vulture.abstractCreature).Extracted[candidate.Side] = true;

        player.room.PlaySound(SoundID.Spear_Dislodged_From_Creature, vulture.bodyChunks[4]);
        player.SlugcatGrab(spear, freeHand);

        Vector2 recoil = direction * 1.6f;
        player.mainBodyChunk.vel -= recoil;
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel -= recoil * 0.7f;
        }
        vulture.bodyChunks[4].vel += recoil * 0.65f;

        return true;
    }

    private static void Tusk_DrawSprites(
        On.KingTusks.Tusk.orig_DrawSprites orig,
        KingTusks.Tusk self,
        VultureGraphics vGraphics,
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        orig(self, vGraphics, sLeaser, rCam, timeStacker, camPos);

        if (self?.vulture?.abstractCreature == null ||
            !IsExtracted(self.vulture.abstractCreature, self.side))
        {
            return;
        }

        HideSprite(sLeaser, self.LaserSprite(vGraphics));
        HideSprite(sLeaser, self.TuskSprite(vGraphics));
        HideSprite(sLeaser, self.TuskDetailSprite(vGraphics));
        HideSprite(sLeaser, vGraphics.TuskWireSprite(self.side));
    }

    private static void HideSprite(RoomCamera.SpriteLeaser sLeaser, int index)
    {
        if (sLeaser?.sprites != null &&
            index >= 0 &&
            index < sLeaser.sprites.Length &&
            sLeaser.sprites[index] != null)
        {
            sLeaser.sprites[index].isVisible = false;
        }
    }

    private static bool IsExtracted(AbstractCreature creature, int side)
    {
        return creature != null &&
               side >= 0 &&
               side < 2 &&
               ExtractionStates.TryGetValue(creature, out VultureExtractionState state) &&
               state.Extracted[side];
    }

    private static Vector2 GetTuskCenter(KingTusks.Tusk tusk)
    {
        return (tusk.chunkPoints[0, 0] + tusk.chunkPoints[1, 0]) * 0.5f;
    }

    private static Vector2 GetTuskDirection(KingTusks.Tusk tusk)
    {
        Vector2 direction = Custom.DirVec(tusk.chunkPoints[1, 0], tusk.chunkPoints[0, 0]);
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = Vector2.right;
        }
        return direction.normalized;
    }

    private static void ResetPull(PlayerPullState state, bool clearReleaseLatch)
    {
        state.Target = null;
        state.Side = -1;
        state.Progress = 0;

        if (clearReleaseLatch)
        {
            state.RequiresRelease = false;
        }
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
            int fromIndex = 5;

            AbstractKingVultureSpear result = new(
                world,
                pos,
                id,
                sourceSide: 0,
                armorColor: Color.Lerp(Color.gray, Color.white, 0.35f),
                colorA: new HSLColor(0f, 0.45f, 0.55f),
                colorB: new HSLColor(0f, 0.85f, 0.45f),
                patternDisplace: 1f,
                profile: new Vector2(0.35f, 0.25f));

            result.stuckInWallCycles = int.Parse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture);
            result.explosive = parts[4] == "1";

            if (ModManager.DLCShared)
            {
                result.hue = float.Parse(parts[5], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.electric = parts[6] == "1";
                result.electricCharge = int.Parse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.needle = parts[8] == "1";
                fromIndex = 9;
            }

            if (ModManager.Watcher)
            {
                result.poison = float.Parse(parts[9], NumberStyles.Any, CultureInfo.InvariantCulture);
                result.poisonHue = float.Parse(parts[10], NumberStyles.Any, CultureInfo.InvariantCulture);
                fromIndex = 11;
            }

            List<string> unrecognized = new();
            for (int i = fromIndex; i < parts.Length; i++)
            {
                string attr = parts[i];

                if (attr.StartsWith(SidePrefix, StringComparison.Ordinal) &&
                    int.TryParse(attr.Substring(SidePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int side))
                {
                    result.SourceSide = Mathf.Clamp(side, 0, 1);
                }
                else if (attr.StartsWith(ArmorPrefix, StringComparison.Ordinal) &&
                         TryParseFloats(attr.Substring(ArmorPrefix.Length), 3, out float[] armor))
                {
                    result.ArmorColor = new Color(armor[0], armor[1], armor[2]);
                }
                else if (attr.StartsWith(ColorAPrefix, StringComparison.Ordinal) &&
                         TryParseFloats(attr.Substring(ColorAPrefix.Length), 3, out float[] a))
                {
                    result.ColorA = new HSLColor(a[0], a[1], a[2]);
                }
                else if (attr.StartsWith(ColorBPrefix, StringComparison.Ordinal) &&
                         TryParseFloats(attr.Substring(ColorBPrefix.Length), 3, out float[] b))
                {
                    result.ColorB = new HSLColor(b[0], b[1], b[2]);
                }
                else if (attr.StartsWith(PatternPrefix, StringComparison.Ordinal) &&
                         float.TryParse(attr.Substring(PatternPrefix.Length), NumberStyles.Any, CultureInfo.InvariantCulture, out float pattern))
                {
                    result.PatternDisplace = Mathf.Clamp01(pattern);
                }
                else if (attr.StartsWith(ProfilePrefix, StringComparison.Ordinal) &&
                         TryParseFloats(attr.Substring(ProfilePrefix.Length), 2, out float[] profile))
                {
                    result.Profile = new Vector2(profile[0], profile[1]);
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
            Plugin.Logger?.LogWarning($"Failed to deserialize KingVultureSpear: {ex}");
            return orig(world, objString);
        }
    }

    private static bool TryParseFloats(string value, int count, out float[] parsed)
    {
        parsed = new float[count];
        string[] pieces = value.Split(',');
        if (pieces.Length != count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(
                    pieces[i],
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out parsed[i]))
            {
                return false;
            }
        }

        return true;
    }
}
