using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;
using Random = UnityEngine.Random;

namespace DryCycle.Items.DewPod;

internal static class DewPodPlantCollisionHooks
{
    private const float SimulationTicksPerSecond = 40f;
    private const float RockHitRadius = 10f;
    private const float SpearHitRadius = 8.5f;
    private const float AttachedPunctureLeakRateWVPerSecond = 25f;
    private const float ExtraDetachedPunctureLeakRateWVPerSecond =
        AttachedPunctureLeakRateWVPerSecond - DewPod.LeakRateWVPerSecond;
    private const string PuncturedAttribute = "DRYCYCLE_DEWPOD_PUNCTURED=1";

    private const int RootSpriteCount = 3;
    private const int SpritesPerPlantSlot = 4;

    private static readonly FieldInfo RuntimeStateField = typeof(DewPodPlant).GetField(
        "_runtimeState",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo ConsumableDataField = typeof(DewPodPlant).GetField(
        "_consumableData",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo TipVelocityField = typeof(DewPodPlant).GetField(
        "_tipVel",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly PropertyInfo LiquidColorProperty = typeof(DewPodPlant).GetProperty(
        "LiquidColor",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class PlantDamageState
    {
        public readonly float[] WaterWV = new float[DewPodPlant.SlotCount];
        public readonly int[] DripCooldown = new int[DewPodPlant.SlotCount];
        public int CycleNumber;
        public int PuncturedMask;
        public int TransferredMask;

        public PlantDamageState(int cycleNumber)
        {
            CycleNumber = cycleNumber;
            for (int i = 0; i < DewPodPlant.SlotCount; i++)
            {
                WaterWV[i] = AbstractDewPod.MaxWaterWV;
                DripCooldown[i] = Random.Range(2, 7);
            }
        }
    }

    private sealed class GameDamageState
    {
        public readonly Dictionary<long, PlantDamageState> Plants = new();
    }

    private sealed class DetachedLeakVisualState
    {
        public int DripCooldown = Random.Range(2, 6);
    }

    private readonly struct PlantHit
    {
        public PlantHit(DewPodPlant plant, int slot, float t, Vector2 point)
        {
            Plant = plant;
            Slot = slot;
            T = t;
            Point = point;
        }

        public DewPodPlant Plant { get; }
        public int Slot { get; }
        public float T { get; }
        public Vector2 Point { get; }
    }

    private sealed class ColoredLeakDrip : WaterDrip
    {
        private readonly Color _liquidColor;

        public ColoredLeakDrip(Vector2 pos, Vector2 vel, Color liquidColor)
            : base(pos, vel, waterColor: false)
        {
            _liquidColor = liquidColor;
            width = 1.5f;
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
            colors = new Color[3]
            {
                Color.Lerp(palette.blackColor, _liquidColor, 0.55f),
                _liquidColor,
                Color.Lerp(_liquidColor, Color.white, 0.72f)
            };
        }
    }

    private static readonly ConditionalWeakTable<RainWorldGame, GameDamageState> GameStates = new();
    private static readonly ConditionalWeakTable<DewPod, DetachedLeakVisualState> DetachedLeakVisuals = new();

    private static bool _enabled;
    private static bool _reflectionFailureLogged;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.Weapon.Update += Weapon_Update;
        On.Room.Update += Room_Update;
        On.RoomCamera.DrawUpdate += RoomCamera_DrawUpdate;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        On.Weapon.Update -= Weapon_Update;
        On.Room.Update -= Room_Update;
        On.RoomCamera.DrawUpdate -= RoomCamera_DrawUpdate;
    }

    private static void Weapon_Update(On.Weapon.orig_Update orig, Weapon self, bool eu)
    {
        if (self?.firstChunk == null)
        {
            orig(self, eu);
            return;
        }

        bool wasThrown = self.mode == Weapon.Mode.Thrown;
        Vector2 traceStart = self.firstChunk.pos;

        orig(self, eu);

        if (!wasThrown ||
            self.room == null ||
            self.firstChunk == null ||
            (self is not Rock && self is not Spear))
        {
            return;
        }

        Vector2 traceEnd = self.firstChunk.pos;
        if ((traceEnd - traceStart).sqrMagnitude < 0.04f)
        {
            return;
        }

        float radius = self is Rock ? RockHitRadius : SpearHitRadius;
        List<PlantHit> hits = CollectPlantHits(self.room, traceStart, traceEnd, radius);
        if (hits.Count == 0)
        {
            return;
        }

        hits.Sort((a, b) => a.T.CompareTo(b.T));

        if (self is Rock rock)
        {
            HandleRockHit(rock, hits[0]);
            return;
        }

        Spear spear = self as Spear;
        for (int i = 0; i < hits.Count; i++)
        {
            HandleSpearPuncture(spear, hits[i]);
        }
    }

    private static List<PlantHit> CollectPlantHits(
        Room room,
        Vector2 start,
        Vector2 end,
        float hitRadius)
    {
        List<PlantHit> hits = new();
        if (room?.updateList == null)
        {
            return hits;
        }

        float radiusSquared = hitRadius * hitRadius;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not DewPodPlant plant || plant.room != room)
            {
                continue;
            }

            for (int slot = 0; slot < DewPodPlant.SlotCount; slot++)
            {
                if (!plant.IsMatureSlot(slot))
                {
                    continue;
                }

                Vector2 podPos = plant.GetPodPosition(slot);
                float distanceSquared = DistanceToSegmentSquared(
                    podPos,
                    start,
                    end,
                    out float t);

                if (distanceSquared <= radiusSquared)
                {
                    hits.Add(new PlantHit(
                        plant,
                        slot,
                        t,
                        Vector2.Lerp(start, end, t)));
                }
            }
        }

        return hits;
    }

    private static float DistanceToSegmentSquared(
        Vector2 point,
        Vector2 start,
        Vector2 end,
        out float t)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.0001f)
        {
            t = 0f;
            return (point - start).sqrMagnitude;
        }

        t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
        Vector2 closest = start + segment * t;
        return (point - closest).sqrMagnitude;
    }

    private static void HandleRockHit(Rock rock, PlantHit hit)
    {
        DewPodPlant plant = hit.Plant;
        if (rock?.room == null || plant == null || !plant.IsMatureSlot(hit.Slot))
        {
            return;
        }

        PlantDamageState damage = GetPlantDamageState(plant);
        if (damage == null)
        {
            return;
        }

        bool wasPunctured = (damage.PuncturedMask & (1 << hit.Slot)) != 0;
        float storedWater = Mathf.Clamp(
            damage.WaterWV[hit.Slot],
            0f,
            AbstractDewPod.MaxWaterWV);
        float waterAfterBurst = Mathf.Max(0f, storedWater - DewPod.BreakBurstLossWV);

        if (!TryDetachBrokenPod(
                plant,
                hit.Slot,
                waterAfterBurst,
                wasPunctured,
                rock.firstChunk.vel,
                out DewPod detachedPod))
        {
            return;
        }

        damage.WaterWV[hit.Slot] = waterAfterBurst;
        if (wasPunctured)
        {
            damage.TransferredMask |= 1 << hit.Slot;
        }

        SpawnBurst(
            rock.room,
            hit.Point,
            detachedPod.LiquidColor,
            rock.firstChunk.vel,
            7);

        Vector2 incomingVelocity = rock.firstChunk.vel;
        rock.ChangeMode(Weapon.Mode.Free);
        rock.firstChunk.vel = incomingVelocity * -0.34f + Custom.RNV() * 1.25f;
        rock.vibrate = 18;
        rock.SetRandomSpin();
        rock.room.PlaySound(SoundID.Rock_Hit_Creature, hit.Point, 0.85f, 1.15f);

        ApplyPlantImpactImpulse(plant, hit.Slot, incomingVelocity * 0.11f);
    }

    private static void HandleSpearPuncture(Spear spear, PlantHit hit)
    {
        DewPodPlant plant = hit.Plant;
        if (spear?.room == null || plant == null || !plant.IsMatureSlot(hit.Slot))
        {
            return;
        }

        PlantDamageState damage = GetPlantDamageState(plant);
        if (damage == null)
        {
            return;
        }

        int bit = 1 << hit.Slot;
        if ((damage.PuncturedMask & bit) != 0)
        {
            return;
        }

        damage.PuncturedMask |= bit;
        damage.DripCooldown[hit.Slot] = 1;

        Color color = GetPlantLiquidColor(plant);
        SpawnBurst(
            spear.room,
            hit.Point,
            color,
            spear.firstChunk.vel,
            4);

        spear.room.PlaySound(SoundID.Spear_Stick_In_Creature, hit.Point, 0.72f, 1.2f);
        ApplyPlantImpactImpulse(plant, hit.Slot, spear.firstChunk.vel * 0.035f);

        // Intentionally do not alter spear mode or velocity. The pod is soft enough
        // that a thrown spear punches through it instead of lodging or bouncing.
    }

    private static void Room_Update(On.Room.orig_Update orig, Room self)
    {
        orig(self);

        if (self?.updateList == null)
        {
            return;
        }

        for (int i = 0; i < self.updateList.Count; i++)
        {
            if (self.updateList[i] is DewPodPlant plant && plant.room == self)
            {
                UpdateAttachedPunctures(plant);
            }
        }

        for (int i = 0; i < self.updateList.Count; i++)
        {
            if (self.updateList[i] is DewPod pod && pod.room == self)
            {
                UpdateDetachedPunctureLeak(pod);
            }
        }
    }

    private static void UpdateAttachedPunctures(DewPodPlant plant)
    {
        PlantDamageState damage = GetPlantDamageState(plant);
        if (damage == null)
        {
            return;
        }

        float leakPerTick = AttachedPunctureLeakRateWVPerSecond / SimulationTicksPerSecond;
        Color liquidColor = GetPlantLiquidColor(plant);

        for (int slot = 0; slot < DewPodPlant.SlotCount; slot++)
        {
            int bit = 1 << slot;
            if ((damage.PuncturedMask & bit) == 0)
            {
                continue;
            }

            if (!plant.IsMatureSlot(slot))
            {
                if ((damage.TransferredMask & bit) == 0 &&
                    TryTransferPunctureToHarvestedPod(plant, slot, damage))
                {
                    damage.TransferredMask |= bit;
                }

                continue;
            }

            if (damage.WaterWV[slot] <= 0f)
            {
                continue;
            }

            damage.WaterWV[slot] = Mathf.Max(0f, damage.WaterWV[slot] - leakPerTick);
            damage.DripCooldown[slot]--;

            if (damage.DripCooldown[slot] <= 0 && plant.room != null)
            {
                Vector2 pos = plant.GetPodPosition(slot);
                Vector2 velocity = new(
                    Random.Range(-0.65f, 0.65f),
                    Random.Range(-2.8f, -0.7f));
                plant.room.AddObject(new ColoredLeakDrip(pos, velocity, liquidColor));
                damage.DripCooldown[slot] = Random.Range(2, 6);
            }
        }
    }

    private static void UpdateDetachedPunctureLeak(DewPod pod)
    {
        if (pod?.AbstrPod == null ||
            !pod.Broken ||
            pod.WaterWV <= 0f ||
            !HasPunctureAttribute(pod.AbstrPod))
        {
            return;
        }

        float extraPerTick = ExtraDetachedPunctureLeakRateWVPerSecond / SimulationTicksPerSecond;
        pod.AbstrPod.WaterWV = Mathf.Max(0f, pod.AbstrPod.WaterWV - extraPerTick);

        DetachedLeakVisualState visual = DetachedLeakVisuals.GetValue(
            pod,
            _ => new DetachedLeakVisualState());
        visual.DripCooldown--;

        if (visual.DripCooldown <= 0 && pod.room != null && pod.AbstrPod.WaterWV > 0f)
        {
            Vector2 origin = pod.firstChunk.pos + new Vector2(
                Random.Range(-2.2f, 2.2f),
                Random.Range(-1.5f, 1.5f));
            Vector2 velocity = pod.firstChunk.vel * 0.08f + new Vector2(
                Random.Range(-0.8f, 0.8f),
                Random.Range(-3.2f, -0.9f));
            pod.room.AddObject(new ColoredLeakDrip(origin, velocity, pod.LiquidColor));
            visual.DripCooldown = Random.Range(2, 5);
        }
    }

    private static bool TryTransferPunctureToHarvestedPod(
        DewPodPlant plant,
        int slot,
        PlantDamageState damage)
    {
        Room room = plant?.room;
        if (room?.updateList == null)
        {
            return false;
        }

        DewPod best = null;
        float bestDistance = float.MaxValue;
        Vector2 sourcePos = plant.GetPodPosition(slot);

        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is not DewPod pod ||
                pod.AbstrPod == null ||
                pod.Broken ||
                pod.AbstrPod.originRoom != plant.OriginRoom ||
                pod.AbstrPod.placedObjectIndex != plant.PlacedObjectIndex)
            {
                continue;
            }

            float distance = Vector2.Distance(sourcePos, pod.firstChunk.pos);
            if (distance < bestDistance && distance <= 55f)
            {
                bestDistance = distance;
                best = pod;
            }
        }

        if (best == null)
        {
            return false;
        }

        best.AbstrPod.WaterWV = Mathf.Clamp(
            damage.WaterWV[slot],
            0f,
            AbstractDewPod.MaxWaterWV);
        best.AbstrPod.Broken = true;
        MarkPunctureAttribute(best.AbstrPod);

        if (best.room != null && best.AbstrPod.WaterWV > 0f)
        {
            SpawnBurst(best.room, best.firstChunk.pos, best.LiquidColor, best.firstChunk.vel, 3);
        }

        return true;
    }

    private static bool TryDetachBrokenPod(
        DewPodPlant plant,
        int slot,
        float waterWV,
        bool preservePuncture,
        Vector2 impactVelocity,
        out DewPod pod)
    {
        pod = null;

        if (plant?.room?.abstractRoom == null ||
            plant.room.world == null ||
            plant.room.game == null ||
            !TryGetPlantInternals(
                plant,
                out DewPodPlantHooks.PlantRuntimeState runtime,
                out PlacedObject.ConsumableObjectData consumableData,
                out Vector2[] tipVelocities))
        {
            return false;
        }

        Room room = plant.room;
        Vector2 spawnPos = plant.GetPodPosition(slot);
        Color liquidColor = GetPlantLiquidColor(plant);

        AbstractDewPod abstractPod = new(
            room.world,
            room.GetWorldCoordinate(spawnPos),
            room.game.GetNewID(),
            plant.OriginRoom,
            plant.PlacedObjectIndex,
            consumableData,
            waterWV,
            broken: true)
        {
            isConsumed = runtime.ConsumptionReported,
            LiquidColor = liquidColor,
            HasLiquidColor = true
        };

        if (preservePuncture)
        {
            MarkPunctureAttribute(abstractPod);
        }

        room.abstractRoom.AddEntity(abstractPod);
        abstractPod.placedObjectOrigin = room.SetAbstractRoomAndPlacedObjectNumber(
            room.abstractRoom.name,
            plant.PlacedObjectIndex);
        abstractPod.RealizeInRoom();

        if (abstractPod.realizedObject is not DewPod realizedPod)
        {
            room.abstractRoom.RemoveEntity(abstractPod);
            return false;
        }

        realizedPod.firstChunk.HardSetPosition(spawnPos);
        realizedPod.firstChunk.lastPos = spawnPos;
        Vector2 inheritedPlantVelocity = slot >= 0 && slot < tipVelocities.Length
            ? tipVelocities[slot]
            : Vector2.zero;
        realizedPod.firstChunk.vel = inheritedPlantVelocity + impactVelocity * 0.34f + Custom.RNV() * 0.6f;

        runtime.HarvestedMask |= 1 << slot;

        if (!runtime.ConsumptionReported)
        {
            abstractPod.isConsumed = false;
            abstractPod.Consume();
            runtime.ConsumptionReported = true;
        }
        else
        {
            abstractPod.isConsumed = true;
        }

        pod = realizedPod;
        return true;
    }

    private static void ApplyPlantImpactImpulse(DewPodPlant plant, int slot, Vector2 impulse)
    {
        if (!TryGetPlantInternals(
                plant,
                out _,
                out _,
                out Vector2[] velocities))
        {
            return;
        }

        for (int i = 0; i < velocities.Length; i++)
        {
            float share = i == slot ? 1f : 0.28f;
            velocities[i] += impulse * share + Custom.RNV() * (i == slot ? 0.35f : 0.12f);
        }
    }

    private static bool TryGetPlantInternals(
        DewPodPlant plant,
        out DewPodPlantHooks.PlantRuntimeState runtime,
        out PlacedObject.ConsumableObjectData consumableData,
        out Vector2[] tipVelocities)
    {
        runtime = null;
        consumableData = null;
        tipVelocities = null;

        if (plant == null ||
            RuntimeStateField == null ||
            ConsumableDataField == null ||
            TipVelocityField == null)
        {
            LogReflectionFailureOnce();
            return false;
        }

        runtime = RuntimeStateField.GetValue(plant) as DewPodPlantHooks.PlantRuntimeState;
        consumableData = ConsumableDataField.GetValue(plant) as PlacedObject.ConsumableObjectData;
        tipVelocities = TipVelocityField.GetValue(plant) as Vector2[];

        if (runtime == null || tipVelocities == null)
        {
            LogReflectionFailureOnce();
            return false;
        }

        return true;
    }

    private static Color GetPlantLiquidColor(DewPodPlant plant)
    {
        if (plant != null && LiquidColorProperty != null)
        {
            object value = LiquidColorProperty.GetValue(plant, null);
            if (value is Color color)
            {
                color.a = 1f;
                return color;
            }
        }

        if (plant?.room != null && DewPod.TryGetLocalWaterColor(plant.room, out Color localColor))
        {
            return localColor;
        }

        return new Color(0.50f, 0.92f, 0.78f);
    }

    private static void SpawnBurst(
        Room room,
        Vector2 origin,
        Color liquidColor,
        Vector2 incomingVelocity,
        int count)
    {
        if (room == null)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            Vector2 velocity = incomingVelocity * Random.Range(-0.08f, 0.04f) +
                               Custom.RNV() * Random.Range(1.3f, 4.6f);
            velocity.y += Random.Range(-1.2f, 1.4f);
            room.AddObject(new ColoredLeakDrip(
                origin + Custom.RNV() * Random.Range(0.5f, 2.4f),
                velocity,
                liquidColor));
        }
    }

    private static PlantDamageState GetPlantDamageState(DewPodPlant plant)
    {
        RainWorldGame game = plant?.room?.game;
        if (game == null)
        {
            return null;
        }

        GameDamageState gameState = GameStates.GetValue(game, _ => new GameDamageState());
        long key = MakeKey(plant.OriginRoom, plant.PlacedObjectIndex);
        int cycleNumber = GetCycleNumber(game);

        if (gameState.Plants.TryGetValue(key, out PlantDamageState existing) &&
            existing.CycleNumber == cycleNumber)
        {
            return existing;
        }

        PlantDamageState created = new(cycleNumber);
        gameState.Plants[key] = created;
        return created;
    }

    private static int GetCycleNumber(RainWorldGame game)
    {
        return game?.GetStorySession?.saveState?.cycleNumber ?? 0;
    }

    private static long MakeKey(int roomIndex, int placedObjectIndex)
    {
        return ((long)roomIndex << 32) | (uint)placedObjectIndex;
    }

    private static void MarkPunctureAttribute(AbstractDewPod pod)
    {
        if (pod == null || HasPunctureAttribute(pod))
        {
            return;
        }

        string[] existing = pod.unrecognizedAttributes ?? Array.Empty<string>();
        string[] updated = new string[existing.Length + 1];
        Array.Copy(existing, updated, existing.Length);
        updated[updated.Length - 1] = PuncturedAttribute;
        pod.unrecognizedAttributes = updated;
    }

    private static bool HasPunctureAttribute(AbstractDewPod pod)
    {
        if (pod?.unrecognizedAttributes == null)
        {
            return false;
        }

        for (int i = 0; i < pod.unrecognizedAttributes.Length; i++)
        {
            if (string.Equals(
                pod.unrecognizedAttributes[i],
                PuncturedAttribute,
                StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void RoomCamera_DrawUpdate(
        On.RoomCamera.orig_DrawUpdate orig,
        RoomCamera self,
        float timeStacker,
        float timeSpeed)
    {
        orig(self, timeStacker, timeSpeed);

        if (self?.room == null || self.spriteLeasers == null)
        {
            return;
        }

        for (int i = 0; i < self.spriteLeasers.Count; i++)
        {
            RoomCamera.SpriteLeaser leaser = self.spriteLeasers[i];
            if (leaser?.drawableObject is not DewPodPlant plant ||
                plant.room != self.room ||
                leaser.sprites == null)
            {
                continue;
            }

            PlantDamageState damage = GetPlantDamageState(plant);
            if (damage == null)
            {
                continue;
            }

            for (int slot = 0; slot < DewPodPlant.SlotCount; slot++)
            {
                if ((damage.PuncturedMask & (1 << slot)) == 0 || !plant.IsMatureSlot(slot))
                {
                    continue;
                }

                int shellIndex = RootSpriteCount + slot * SpritesPerPlantSlot + 1;
                int liquidIndex = RootSpriteCount + slot * SpritesPerPlantSlot + 2;
                int accentIndex = RootSpriteCount + slot * SpritesPerPlantSlot + 3;
                if (shellIndex >= leaser.sprites.Length ||
                    liquidIndex >= leaser.sprites.Length ||
                    accentIndex >= leaser.sprites.Length)
                {
                    continue;
                }

                FSprite shell = leaser.sprites[shellIndex];
                FSprite liquid = leaser.sprites[liquidIndex];
                FSprite damageMark = leaser.sprites[accentIndex];
                if (shell == null || liquid == null || damageMark == null)
                {
                    continue;
                }

                float fill = Mathf.Clamp01(damage.WaterWV[slot] / AbstractDewPod.MaxWaterWV);
                float roundedFill = Mathf.Sqrt(fill);

                liquid.isVisible = fill > 0.005f;
                liquid.scaleX = shell.scaleX * Mathf.Lerp(0.48f, 0.76f, roundedFill);
                liquid.scaleY = shell.scaleY * Mathf.Lerp(0.08f, 0.86f, fill);

                Vector2 direction = Custom.DirVec(plant.RootPos, plant.GetPodPosition(slot));
                if (direction.sqrMagnitude < 0.001f)
                {
                    direction = Vector2.up;
                }

                Vector2 perpendicular = Custom.PerpendicularVector(direction);
                float sink = Mathf.Lerp(3.6f, 0f, roundedFill);
                liquid.x = shell.x - direction.x * sink;
                liquid.y = shell.y - direction.y * sink;

                // The fleshy wall relaxes slightly as water escapes, while
                // retaining the same overall cylindrical identity.
                shell.scaleX *= Mathf.Lerp(0.82f, 1f, roundedFill);
                shell.scaleY *= Mathf.Lerp(0.88f, 1f, roundedFill);
                shell.color = Color.Lerp(shell.color, self.currentPalette.blackColor, 0.12f);

                // Reuse the mature pod's highlight sprite as a puncture scar after
                // the spear has passed through. Position, angle, and size are
                // deterministic-random per plant/slot: they vary between pods but
                // remain stable frame-to-frame and across room redraws.
                float sideOffset = Mathf.Lerp(
                    -2.55f,
                    2.55f,
                    StableDamageRandom01(plant, slot, 11));
                float axialOffset = Mathf.Lerp(
                    -3.15f,
                    3.15f,
                    StableDamageRandom01(plant, slot, 12));
                Vector2 damagePos = new(shell.x, shell.y);
                damagePos += perpendicular * sideOffset + direction * axialOffset;

                damageMark.isVisible = true;
                damageMark.x = damagePos.x;
                damageMark.y = damagePos.y;
                damageMark.scaleX = shell.scaleX * Mathf.Lerp(
                    0.055f,
                    0.095f,
                    StableDamageRandom01(plant, slot, 13));
                damageMark.scaleY = shell.scaleY * Mathf.Lerp(
                    0.24f,
                    0.42f,
                    StableDamageRandom01(plant, slot, 14));
                damageMark.rotation = shell.rotation + Mathf.Lerp(
                    -62f,
                    62f,
                    StableDamageRandom01(plant, slot, 15));
                damageMark.alpha = 0.96f;
                damageMark.color = self.currentPalette.blackColor;
            }
        }
    }

    private static float StableDamageRandom01(DewPodPlant plant, int slot, int salt)
    {
        unchecked
        {
            int seed = plant?.OriginRoom ?? 0;
            seed = seed * 397 ^ (plant?.PlacedObjectIndex ?? 0);
            seed = seed * 397 ^ slot;
            seed = seed * 397 ^ salt;

            uint value = (uint)seed;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;

            return (value & 0x00FFFFFFu) / 16777215f;
        }
    }

    private static void LogReflectionFailureOnce()
    {
        if (_reflectionFailureLogged)
        {
            return;
        }

        _reflectionFailureLogged = true;
        Plugin.Logger?.LogWarning(
            "DewPodPlant collision interaction could not access the plant runtime fields; " +
            "weapon impacts on attached pods will be skipped.");
    }
}
