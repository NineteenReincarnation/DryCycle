using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal struct DesertBatflyThreatCue
{
    internal int PlayerSlot;
    internal bool VisibleSpear;
    internal bool VisibleRock;
    internal bool VisibleExplosive;
    internal bool VisibleStartle;
    internal bool VisibleShock;
    internal bool RecentSpearThrow;
    internal bool RecentRockThrow;
    internal bool RecentExplosion;
    internal bool RecentGrabAttempt;
    internal bool ProjectileThreat;
    internal Vector2 ProjectileThreatDirection;
    internal Vector2? CurrentHazardCenter;
    internal bool PlayerRetreating;
}

internal struct DesertBatflyThreatDebugState
{
    internal int PlayerSlot;
    internal float Confidence;
    internal float ProjectilePressure;
    internal float PiercingPressure;
    internal float BluntStunPressure;
    internal float ExplosionPressure;
    internal float StartlePressure;
    internal float ShockPressure;
    internal float AreaDenialPressure;
    internal float GrabCapturePressure;
    internal float PursuitPressure;
    internal float CounterKillPressure;
    internal float RetreatTendency;
    internal float NonAggressionConfidence;
    internal string DominantSignature;

    internal DesertBatflyThreatCue Cue;
    internal int AcuteExplosionTimer;
    internal int AcuteStartleTimer;
    internal int AcuteMassCasualtyTimer;
    internal int AcuteCaptureTimer;
    internal int AcuteShockTimer;
    internal Vector2? HazardCenter;

    internal string ModifierReason;
    internal string AttackGeometryAdjustment;
    internal float AttachSuppression;
    internal Vector2? EvadeTarget;
    internal string LastEvidenceType;
    internal float LastEvidenceStrength;
    internal int LastEvidencePlayerSlot;
    internal string LastWitnessReason;
}

internal static class DesertBatflyThreatRuntime
{
    internal const int CueRefreshTicks = 12;
    internal const int ProjectileCueTicks = 90;
    internal const int ExplosionCueTicks = 150;
    internal const int GrabCueTicks = 100;
    internal const int FormalAggressionMemoryTicks = 220;
    internal const int RecentDamageMemoryTicks = DB_EventHub.MortalityAttributionTicks;
    internal const int PursuitMinimumTicks = 48;
    internal const int RetreatMinimumTicks = 64;
    internal const int NonAggressionEncounterTicks = 260;

    private const float WitnessNearRadius = 240f;
    private const float WitnessFarRadius = 390f;
    private const float ProjectileNearMissRadius = 42f;
    private const float ProjectileNearMissMaxDistance = 230f;

    private sealed class RuntimeState
    {
        internal int CueRefresh;
        internal DesertBatflyThreatCue Cue;

        // Short-lived Threat evidence cache only. Mortality attribution belongs exclusively
        // to DB_EventHub and is never reconstructed from these fields.
        internal int RecentDamagePlayerSlot = -1;
        internal int RecentDamageTick = int.MinValue;
        internal DesertBatflyThreatEvidence RecentDamageEvidence;

        internal int FormalAggressionPlayerSlot = -1;
        internal int FormalAggressionTick = int.MinValue;

        internal int PursuitPlayerSlot = -1;
        internal int PursuitTicks;
        internal float PursuitLastDistance = -1f;
        internal bool PursuitAwarded;
        internal int EscapeThreatPlayerSlot = -1;
        internal bool PursuitDisengageExtended;

        internal int EncounterPlayerSlot = -1;
        internal int EncounterTicks;
        internal int RetreatTicks;
        internal float EncounterLastDistance = -1f;
        internal bool RetreatAwarded;
        internal int NonAggressionAwards;

        internal int LastNearMissWeaponHash = int.MinValue;
        internal int LastNearMissTick = int.MinValue;

        internal int AcuteExplosionTimer;
        internal int AcuteStartleTimer;
        internal int AcuteMassCasualtyTimer;
        internal int AcuteCaptureTimer;
        internal int AcuteShockTimer;
        internal Vector2? HazardCenter;
        internal int HazardTimer;
        internal Player AcuteInstigator;

        internal DesertBatflyAI.Activity PreviousMode;

        internal string ModifierReason = string.Empty;
        internal string AttackGeometryAdjustment = string.Empty;
        internal float AttachSuppression;
        internal Vector2? EvadeTarget;
        internal string LastEvidenceType = string.Empty;
        internal float LastEvidenceStrength;
        internal int LastEvidencePlayerSlot = -1;
        internal string LastWitnessReason = string.Empty;
    }

    private sealed class RoomState
    {
        // Threat-owned temporal evidence only. Players/weapons belong to DB_RoomContext.
        internal readonly int[] RecentSpearThrow = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        internal readonly int[] RecentRockThrow = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        internal readonly int[] RecentExplosion = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        internal readonly int[] RecentGrab = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        internal readonly int[] CasualtyWindowStart = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        internal readonly int[] CasualtyCount = new int[4];

        internal void RecordThrow(Weapon weapon, Player player, int clock)
        {
            int slot = PlayerSlot(player);
            if (!ValidSlot(slot) || weapon == null) return;
            if (weapon is Spear) RecentSpearThrow[slot] = clock;
            if (weapon is Rock) RecentRockThrow[slot] = clock;
        }

        internal void RecordExplosion(Player player, int clock)
        {
            int slot = PlayerSlot(player);
            if (ValidSlot(slot)) RecentExplosion[slot] = clock;
        }

        internal void RecordGrab(Player player, int clock)
        {
            int slot = PlayerSlot(player);
            if (ValidSlot(slot)) RecentGrab[slot] = clock;
        }

        internal bool RecordCasualty(Player player, int clock)
        {
            int slot = PlayerSlot(player);
            if (!ValidSlot(slot)) return false;
            if (CasualtyWindowStart[slot] == int.MinValue || clock < CasualtyWindowStart[slot] ||
                clock - CasualtyWindowStart[slot] > 320)
            {
                CasualtyWindowStart[slot] = clock;
                CasualtyCount[slot] = 1;
                return false;
            }
            CasualtyCount[slot]++;
            return CasualtyCount[slot] >= 2;
        }
    }

    private sealed class ProcessedExplosion { }

    private sealed class SourceOwner
    {
        internal Player Player;
        internal int Clock;
    }

    private static ConditionalWeakTable<DesertBatfly, RuntimeState> states = new();
    private static ConditionalWeakTable<Room, RoomState> roomStates = new();
    private static ConditionalWeakTable<Explosion, ProcessedExplosion> processedExplosions = new();
    private static ConditionalWeakTable<PhysicalObject, SourceOwner> sourceOwners = new();
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        Reset();
        DB_EventHub.Damage += DamageEvent;
        DB_EventHub.Capture += CaptureEvent;
        DB_EventHub.Mortality += MortalityEvent;
        On.Weapon.Thrown += WeaponThrown;
        On.Explosion.Update += ExplosionUpdate;
        On.FirecrackerPlant.PopLump += FirecrackerPopLump;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        DB_EventHub.Damage -= DamageEvent;
        DB_EventHub.Capture -= CaptureEvent;
        DB_EventHub.Mortality -= MortalityEvent;
        On.Weapon.Thrown -= WeaponThrown;
        On.Explosion.Update -= ExplosionUpdate;
        On.FirecrackerPlant.PopLump -= FirecrackerPopLump;
        Reset();
    }

    internal static void Reset()
    {
        states = new ConditionalWeakTable<DesertBatfly, RuntimeState>();
        roomStates = new ConditionalWeakTable<Room, RoomState>();
        processedExplosions = new ConditionalWeakTable<Explosion, ProcessedExplosion>();
        sourceOwners = new ConditionalWeakTable<PhysicalObject, SourceOwner>();
        DB_ThreatMemoryStore.ResetRuntime();
    }

    internal static void Forget(DesertBatfly bat)
    {
        if (bat != null) states.Remove(bat);
    }

    internal static void RefreshState(DesertBatfly bat)
    {
        if (bat == null || bat.room == null || bat.dead || bat.slatedForDeletetion) return;
        RuntimeState state = StateFor(bat);
        DB_ThreatMemoryStore.DecayToCycle(bat.DesertState, CurrentCycle(bat));
        TickAcute(state);
        TrackFormalAggression(bat, state);
        UpdateCue(bat, state);
        ApplyHeldThreatPriority(bat, state);
        ExtendLearnedDisengage(bat, state);
        TrackPursuit(bat, state);
        TrackEncounter(bat, state);
    }

    // Compatibility state-only surface. R3 hooks call RefreshState before arbitration.
    internal static void Update(DesertBatfly bat) => RefreshState(bat);

    internal static void ApplyOwnedTacticalModifier(DesertBatfly bat)
    {
        if (bat == null || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat) ||
            !states.TryGetValue(bat, out RuntimeState state))
            return;
        ApplyTacticalAdjustment(bat, state);
    }

    internal static void CommitFrame(DesertBatfly bat)
    {
        if (bat != null && states.TryGetValue(bat, out RuntimeState state))
            state.PreviousMode = bat.DesertAI.Mode;
    }

    internal static bool TryGetDebugState(DesertBatfly bat, out DesertBatflyThreatDebugState debug)
    {
        debug = default;
        if (bat == null) return false;
        RuntimeState state = StateFor(bat);
        int slot = state.Cue.PlayerSlot;
        DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(bat.DesertState, slot);
        debug.PlayerSlot = slot;
        if (memory != null)
        {
            debug.Confidence = memory.Confidence;
            debug.ProjectilePressure = memory.ProjectilePressure;
            debug.PiercingPressure = memory.PiercingPressure;
            debug.BluntStunPressure = memory.BluntStunPressure;
            debug.ExplosionPressure = memory.ExplosionPressure;
            debug.StartlePressure = memory.StartlePressure;
            debug.ShockPressure = memory.ShockPressure;
            debug.AreaDenialPressure = memory.AreaDenialPressure;
            debug.GrabCapturePressure = memory.GrabCapturePressure;
            debug.PursuitPressure = memory.PursuitPressure;
            debug.CounterKillPressure = memory.CounterKillPressure;
            debug.RetreatTendency = memory.RetreatTendency;
            debug.NonAggressionConfidence = memory.NonAggressionConfidence;
            debug.DominantSignature = DB_ThreatMemoryStore.DominantSignature(memory);
        }
        else debug.DominantSignature = "None";

        debug.Cue = state.Cue;
        debug.AcuteExplosionTimer = state.AcuteExplosionTimer;
        debug.AcuteStartleTimer = state.AcuteStartleTimer;
        debug.AcuteMassCasualtyTimer = state.AcuteMassCasualtyTimer;
        debug.AcuteCaptureTimer = state.AcuteCaptureTimer;
        debug.AcuteShockTimer = state.AcuteShockTimer;
        debug.HazardCenter = state.HazardCenter;
        debug.ModifierReason = state.ModifierReason;
        debug.AttackGeometryAdjustment = state.AttackGeometryAdjustment;
        debug.AttachSuppression = state.AttachSuppression;
        debug.EvadeTarget = state.EvadeTarget;
        debug.LastEvidenceType = state.LastEvidenceType;
        debug.LastEvidenceStrength = state.LastEvidenceStrength;
        debug.LastEvidencePlayerSlot = state.LastEvidencePlayerSlot;
        debug.LastWitnessReason = state.LastWitnessReason;
        return true;
    }

    private static void DamageEvent(DB_DamageEvent damageEvent)
    {
        DesertBatfly bat = damageEvent.Victim;
        if (bat == null) return;

        Player player = damageEvent.Instigator as Player ??
                        ResolvePlayer(damageEvent.SourceObject, null);
        if (player == null) return;

        bool projectile = damageEvent.SourceObject is Weapon weapon &&
                          ResolvePlayer(weapon, null) == player;
        DesertBatflyThreatEvidence evidence = DesertBatflyThreatAdapterRegistry.Classify(
            damageEvent.SourceObject,
            damageEvent.DamageType,
            damageEvent.Damage,
            damageEvent.Stun,
            projectile);
        if (!evidence.Any) return;

        AddEvidence(bat, player, evidence, 1f, "direct hit", false);
        RuntimeState state = StateFor(bat);
        state.RecentDamagePlayerSlot = PlayerSlot(player);
        state.RecentDamageTick = damageEvent.Clock;
        state.RecentDamageEvidence = evidence;

        if (damageEvent.DamageType == Creature.DamageType.Electric)
        {
            state.AcuteShockTimer = Mathf.Max(state.AcuteShockTimer, 150);
            state.AcuteInstigator = player;
        }

        // A lethal hit is followed by the canonical MortalityEvent. Broadcasting a second
        // generic witnessed-hit event here would train the same fact twice.
        if (damageEvent.Lethal || bat.dead || bat.slatedForDeletetion) return;

        var threatEvent = new DesertBatflyThreatEvent(
            player,
            damageEvent.SourceObject,
            bat,
            damageEvent.Position,
            evidence,
            true,
            false,
            damageEvent.Stun,
            "witnessed hit");
        BroadcastWitnessEvidence(threatEvent, 0.48f);
    }

    private static void CaptureEvent(DB_CaptureEvent captureEvent)
    {
        DesertBatfly bat = captureEvent.Victim;
        if (bat == null || bat.dead || captureEvent.Captor is not Player player)
            return;

        AddEvidence(
            bat,
            player,
            DesertBatflyThreatAdapterRegistry.GrabEvidence(),
            1f,
            "player capture",
            false);
        RuntimeState state = StateFor(bat);
        state.AcuteCaptureTimer = Mathf.Max(state.AcuteCaptureTimer, 150);
        state.AcuteInstigator = player;
        if (bat.room != null && captureEvent.Clock != int.MinValue)
            RoomFor(bat.room).RecordGrab(player, captureEvent.Clock);
    }

    private static void MortalityEvent(DB_MortalityEvent mortalityEvent)
    {
        DesertBatfly bat = mortalityEvent.Victim;
        if (bat == null) return;

        RuntimeState state = StateFor(bat);
        try
        {
            if (mortalityEvent.Killer is not Player killer)
                return;

            int slot = PlayerSlot(killer);
            if (!ValidSlot(slot)) return;

            bool counterKill =
                state.FormalAggressionPlayerSlot == slot &&
                Recent(state.FormalAggressionTick, mortalityEvent.Clock, FormalAggressionMemoryTicks);

            DesertBatflyThreatEvidence killEvidence = default;
            if (state.RecentDamagePlayerSlot == slot &&
                Recent(state.RecentDamageTick, mortalityEvent.Clock, RecentDamageMemoryTicks))
            {
                killEvidence = state.RecentDamageEvidence;
            }

            if (!killEvidence.Any && mortalityEvent.WasConsumed)
                killEvidence = DesertBatflyThreatAdapterRegistry.GrabEvidence();

            if (!killEvidence.Any && mortalityEvent.SourceObject != null)
            {
                bool projectile = mortalityEvent.SourceObject is Weapon weapon &&
                                  ResolvePlayer(weapon, null) == killer;
                killEvidence = DesertBatflyThreatAdapterRegistry.Classify(
                    mortalityEvent.SourceObject,
                    mortalityEvent.DamageType,
                    mortalityEvent.Damage,
                    mortalityEvent.Stun,
                    projectile);
            }

            if (counterKill)
                killEvidence.Merge(DesertBatflyThreatAdapterRegistry.CounterKillEvidence());

            var threatEvent = new DesertBatflyThreatEvent(
                killer,
                mortalityEvent.SourceObject,
                bat,
                mortalityEvent.Position,
                killEvidence,
                true,
                true,
                0f,
                counterKill ? "counter kill" :
                    mortalityEvent.WasConsumed ? "player consumption kill" : "player kill");
            BroadcastWitnessEvidence(threatEvent, 0.72f);

            Room room = bat.room;
            if (room != null && RoomFor(room).RecordCasualty(killer, mortalityEvent.Clock))
                BroadcastMassCasualty(room, killer, mortalityEvent.Position);
        }
        finally
        {
            // This domain owns its realized state cleanup. Generic mortality consumers do
            // not clear it before the canonical kill evidence has been processed.
            Forget(bat);
        }
    }

    private static void WeaponThrown(
        On.Weapon.orig_Thrown orig,
        Weapon self,
        Creature thrownBy,
        Vector2 thrownPos,
        Vector2? firstFrameTraceFromPos,
        IntVector2 throwDir,
        float frc,
        bool eu)
    {
        orig(self, thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, frc, eu);
        if (thrownBy is not Player player || self == null) return;
        int clock = self.room?.game?.clock ?? int.MinValue;
        RememberSourceOwner(self, player, clock);
        if (clock != int.MinValue)
            RoomFor(self.room).RecordThrow(self, player, clock);
    }

    private static void ExplosionUpdate(On.Explosion.orig_Update orig, Explosion self, bool eu)
    {
        if (self != null && self.room != null && !processedExplosions.TryGetValue(self, out _))
        {
            processedExplosions.Add(self, new ProcessedExplosion());
            ReportExplosion(self);
        }
        orig(self, eu);
    }

    private static void FirecrackerPopLump(
        On.FirecrackerPlant.orig_PopLump orig,
        FirecrackerPlant self,
        int lmp)
    {
        orig(self, lmp);
        if (self?.room == null || lmp < 0 || self.lumps == null || lmp >= self.lumps.Length)
            return;
        BroadcastStartle(
            self.room,
            ResolvePlayer(self, null),
            self,
            self.lumps[lmp].pos);
    }

    private static void ReportExplosion(Explosion explosion)
    {
        Room room = explosion.room;
        if (room == null) return;
        Player player = ResolvePlayer(explosion.sourceObject, explosion.killTagHolder);
        int clock = room.game?.clock ?? int.MinValue;
        if (player != null && clock != int.MinValue)
            RoomFor(room).RecordExplosion(player, clock);

        DesertBatflyThreatEvidence evidence =
            DesertBatflyThreatAdapterRegistry.ExplosionEvidence(explosion);
        float acuteRadius = Mathf.Max(190f, explosion.rad * 1.55f);

        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat.dead || bat.room != room || !bat.Consious)
                continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, explosion.pos);
            if (distance > acuteRadius) continue;

            RuntimeState state = StateFor(bat);
            float proximity = Mathf.InverseLerp(acuteRadius, 20f, distance);
            state.AcuteExplosionTimer = Mathf.Max(
                state.AcuteExplosionTimer,
                Mathf.RoundToInt(Mathf.Lerp(80f, 220f, proximity)));
            state.HazardCenter = explosion.pos;
            state.HazardTimer = Mathf.Max(state.HazardTimer, 220);
            state.AcuteInstigator = player;
            DesertBatflySocialLife.CancelForPriority(bat, "Task11 acute explosion");
            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))
                bat.DesertAI.ThreatenedAt(player, explosion.pos, false, false);

            if (player != null && evidence.Any &&
                DB_VisibilityPolicy.CanObserve(
                    bat, explosion.pos, acuteRadius, DB_VisibilityChannel.Signal))
            {
                float witness = distance <= explosion.rad ? 0.62f : 0.34f;
                AddEvidence(bat, player, evidence, witness, "witnessed explosion", true);
            }
        }

        float alarmIntensity = Mathf.Clamp01(
            0.60f + Mathf.Clamp01(explosion.damage) * 0.16f +
            Mathf.InverseLerp(80f, 360f, explosion.rad) * 0.16f);
        DB_SignalRuntime.EmitAcuteAlarm(
            room,
            explosion.killTagHolder,
            explosion.pos,
            Mathf.Max(0.62f, alarmIntensity),
            "Task11 acute explosion -> Task12 AlarmFlutter at real explosion center");
    }

    private static void BroadcastStartle(
        Room room,
        Player player,
        FirecrackerPlant source,
        Vector2 position)
    {
        if (room == null) return;
        DesertBatflyThreatEvidence evidence =
            DesertBatflyThreatAdapterRegistry.FirecrackerStartleEvidence();
        const float acuteRadius = 270f;

        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat.dead || bat.room != room || !bat.Consious)
                continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, position);
            if (distance > acuteRadius) continue;

            RuntimeState state = StateFor(bat);
            float proximity = Mathf.InverseLerp(acuteRadius, 35f, distance);
            state.AcuteStartleTimer = Mathf.Max(
                state.AcuteStartleTimer,
                Mathf.RoundToInt(Mathf.Lerp(70f, 190f, proximity)));
            state.HazardCenter = position;
            state.HazardTimer = Mathf.Max(state.HazardTimer, 110);
            state.AcuteInstigator = player;
            DesertBatflySocialLife.CancelForPriority(bat, "Task11 acute startle");
            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))
                bat.DesertAI.ThreatenedAt(player, position, false, false);

            if (player != null && DB_VisibilityPolicy.CanObserve(
                    bat, position, acuteRadius, DB_VisibilityChannel.Signal))
            {
                float multiplier = distance <= 90f ? 0.88f : distance <= 190f ? 0.52f : 0.30f;
                AddEvidence(bat, player, evidence, multiplier, "firecracker startle", true);
            }
        }

        DB_SignalRuntime.EmitAcuteAlarm(
            room, player, position, 0.82f,
            "Task11 firecracker/startle -> Task12 AlarmFlutter at real startle center");
    }

    private static void BroadcastMassCasualty(Room room, Player player, Vector2 position)
    {
        if (room == null || player == null) return;
        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is not DesertBatfly bat || bat.dead || bat.room != room || !bat.Consious)
                continue;
            if (!Custom.DistLess(bat.mainBodyChunk.pos, position, 470f) &&
                !Custom.DistLess(bat.mainBodyChunk.pos, player.mainBodyChunk.pos, 470f))
                continue;

            RuntimeState state = StateFor(bat);
            state.AcuteMassCasualtyTimer = Mathf.Max(state.AcuteMassCasualtyTimer, 300);
            state.AcuteInstigator = player;
            DesertBatflySocialLife.CancelForPriority(bat, "Task11 acute mass casualty");
            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))
                bat.DesertAI.ThreatenedAt(player, position, false, false);
        }

        DB_SignalRuntime.EmitAcuteAlarm(
            room, player, position, 0.96f,
            "Task11 mass casualty -> high urgency Task12 AlarmFlutter");
    }

    private static void BroadcastWitnessEvidence(
        in DesertBatflyThreatEvent threatEvent,
        float baseMultiplier)
    {
        Room room = threatEvent.Victim?.room;
        Player player = threatEvent.Instigator;
        if (room == null || player == null || player.room != room || !threatEvent.Evidence.Any)
            return;

        foreach (Fly other in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (other is not DesertBatfly witness || witness == threatEvent.Victim || witness.dead ||
                witness.room != room || !witness.Consious)
                continue;
            float distance = Vector2.Distance(witness.mainBodyChunk.pos, threatEvent.Position);
            if (distance > WitnessFarRadius ||
                !DB_VisibilityPolicy.CanObserve(
                    witness, threatEvent.Position, WitnessFarRadius, DB_VisibilityChannel.Signal))
                continue;

            float distanceMultiplier = distance <= WitnessNearRadius
                ? Mathf.Lerp(0.65f, 0.45f, Mathf.InverseLerp(0f, WitnessNearRadius, distance))
                : Mathf.Lerp(0.35f, 0.20f, Mathf.InverseLerp(WitnessNearRadius, WitnessFarRadius, distance));
            float bond = threatEvent.Victim != null
                ? witness.DesertState.BondStrength(threatEvent.Victim.abstractCreature.ID)
                : 0f;
            float bondBonus = Mathf.Lerp(1f, 1.16f, bond);
            AddEvidence(
                witness,
                player,
                threatEvent.Evidence,
                baseMultiplier * distanceMultiplier * bondBonus,
                threatEvent.Reason,
                true);
        }
    }

    private static void AddEvidence(
        DesertBatfly bat,
        Player player,
        in DesertBatflyThreatEvidence evidence,
        float multiplier,
        string reason,
        bool witness)
    {
        if (bat == null || player == null || !evidence.Any) return;
        int slot = PlayerSlot(player);
        if (!ValidSlot(slot)) return;

        DB_ThreatMemoryStore.AddEvidence(
            bat.DesertState,
            slot,
            evidence,
            multiplier,
            CurrentCycle(bat));
        RuntimeState state = StateFor(bat);
        state.LastEvidenceType = evidence.Tags.ToString();
        state.LastEvidenceStrength = Mathf.Clamp01(evidence.Strongest * multiplier);
        state.LastEvidencePlayerSlot = slot;
        state.LastWitnessReason = witness ? reason : "direct: " + reason;
    }

    private static void UpdateCue(DesertBatfly bat, RuntimeState state)
    {
        if (state.CueRefresh > 0)
        {
            state.CueRefresh--;
            return;
        }
        state.CueRefresh = CueRefreshTicks;

        Room room = bat.room;
        RoomState roomState = RoomFor(room);
        DB_RoomContext context = DB_RoomContext.For(room);
        if (context == null)
        {
            state.Cue = default;
            return;
        }

        Player player = bat.DesertAI.Target as Player;
        if (player == null || player.dead || player.room != room ||
            !DB_VisibilityPolicy.CanObserve(
                bat, player.mainBodyChunk.pos, 430f, DB_VisibilityChannel.Player))
            player = NearestVisiblePlayer(bat, context.Players);

        DesertBatflyThreatCue cue = default;
        cue.PlayerSlot = PlayerSlot(player);
        if (player == null || !ValidSlot(cue.PlayerSlot))
        {
            state.Cue = cue;
            return;
        }

        if (DB_WeaponPerception.TryObserveHeldThreats(
                bat, player, 430f, out DB_HeldThreatObservation held))
        {
            cue.VisibleSpear = held.VisibleSpear;
            cue.VisibleRock = held.VisibleRock;
            cue.VisibleExplosive = held.VisibleExplosive;
            cue.VisibleStartle = held.VisibleStartle;
            cue.VisibleShock = held.VisibleShock;
        }

        int clock = room.game?.clock ?? 0;
        cue.RecentSpearThrow = Recent(roomState.RecentSpearThrow[cue.PlayerSlot], clock, ProjectileCueTicks);
        cue.RecentRockThrow = Recent(roomState.RecentRockThrow[cue.PlayerSlot], clock, ProjectileCueTicks);
        cue.RecentExplosion = Recent(roomState.RecentExplosion[cue.PlayerSlot], clock, ExplosionCueTicks);
        cue.RecentGrabAttempt = Recent(roomState.RecentGrab[cue.PlayerSlot], clock, GrabCueTicks);
        cue.CurrentHazardCenter = state.HazardTimer > 0 ? state.HazardCenter : null;
        cue.PlayerRetreating = state.EncounterPlayerSlot == cue.PlayerSlot && state.RetreatTicks > 12;

        if (DB_WeaponPerception.TryFindIncomingProjectileFrom(
                bat,
                player,
                ProjectileNearMissMaxDistance,
                ProjectileNearMissRadius,
                16f,
                out DB_WeaponObservation projectile))
        {
            cue.ProjectileThreat = true;
            cue.ProjectileThreatDirection = projectile.Velocity.normalized;
            LearnNearMiss(bat, state, projectile.Weapon, player, clock);
        }

        state.Cue = cue;
        if (cue.ProjectileThreat)
        {
            // Real trajectory is a current-frame Arbiter fact. Do not pre-promote it into
            // DesertAI Escape here or ImmediateDanger would starve ProjectileEvade.
            DesertBatflySocialLife.CancelForPriority(bat, "Task11 incoming projectile");
            state.ModifierReason = "real incoming projectile queued for R3 arbitration";
        }
    }

    private static void ApplyHeldThreatPriority(DesertBatfly bat, RuntimeState state)
    {
        DesertBatflyThreatCue cue = state.Cue;
        if (!ValidSlot(cue.PlayerSlot) || bat.room == null) return;
        DB_PlayerThreatMemory memory =
            DB_ThreatMemoryStore.For(bat.DesertState, cue.PlayerSlot);
        if (memory == null || memory.Confidence < 0.08f) return;

        Player player = DB_RoomContext.For(bat.room)?.PlayerBySlot(cue.PlayerSlot);
        if (player == null) return;

        float heldRisk = 0f;
        if (cue.VisibleSpear) heldRisk += memory.PiercingPressure * 0.48f;
        if (cue.VisibleRock) heldRisk += memory.BluntStunPressure * 0.24f;
        if (cue.VisibleExplosive) heldRisk += memory.ExplosionPressure * 0.65f;
        if (cue.VisibleStartle) heldRisk += memory.StartlePressure * 0.46f;
        if (cue.VisibleShock) heldRisk += memory.ShockPressure * 0.50f;
        heldRisk *= Mathf.Lerp(1.15f, 0.72f, bat.Personality.Nerve);
        heldRisk *= Mathf.Lerp(0.70f, 1f, memory.Confidence);
        heldRisk = Mathf.Clamp01(heldRisk);
        if (heldRisk < 0.22f) return;

        float distance = Vector2.Distance(bat.mainBodyChunk.pos, player.mainBodyChunk.pos);
        if (distance < Mathf.Lerp(170f, 260f, heldRisk))
            DesertBatflySocialLife.CancelForPriority(bat, "Task11 learned held-item caution");

        if (heldRisk >= 0.72f && distance < 155f &&
            !DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) &&
            bat.DesertAI.Target == null)
        {
            // Learned Threat memory may suppress/reshape aggression, but it never creates a
            // locomotion owner by itself. Direct real danger uses the ordinary danger/fear path.
            state.ModifierReason = "recognized held threat; learned caution remains a modifier";
        }
    }

    private static void LearnNearMiss(
        DesertBatfly bat,
        RuntimeState state,
        Weapon weapon,
        Player player,
        int clock)
    {
        int hash = WeaponIdentity(weapon);
        if (state.LastNearMissWeaponHash == hash && state.LastNearMissTick != int.MinValue &&
            clock >= state.LastNearMissTick && clock - state.LastNearMissTick < 120)
            return;

        state.LastNearMissWeaponHash = hash;
        state.LastNearMissTick = clock;
        DesertBatflyThreatEvidence evidence = DesertBatflyThreatAdapterRegistry.Classify(
            weapon, null, 0f, 0f, true);
        AddEvidence(bat, player, evidence, 0.28f, "projectile near miss", false);
    }

    private static void TrackFormalAggression(DesertBatfly bat, RuntimeState state)
    {
        if (bat.DesertAI.Target is not Player player) return;
        DesertBatflyAI.Activity mode = bat.DesertAI.Mode;
        bool formal = bat.DesertAI.FormalAttack || mode is
            DesertBatflyAI.Activity.Approach or DesertBatflyAI.Activity.Circle or
            DesertBatflyAI.Activity.FakeDive or DesertBatflyAI.Activity.Dive or
            DesertBatflyAI.Activity.Attach or DesertBatflyAI.Activity.RetaliationCharge or
            DesertBatflyAI.Activity.Interfere;
        if (!formal) return;
        state.FormalAggressionPlayerSlot = PlayerSlot(player);
        state.FormalAggressionTick = bat.room?.game?.clock ?? int.MinValue;
    }

    private static void TrackPursuit(DesertBatfly bat, RuntimeState state)
    {
        if (bat.DesertAI.Mode != DesertBatflyAI.Activity.Escape || bat.room == null)
        {
            state.PursuitTicks = 0;
            state.PursuitLastDistance = -1f;
            state.PursuitAwarded = false;
            state.PursuitPlayerSlot = -1;
            return;
        }

        if (state.PreviousMode != DesertBatflyAI.Activity.Escape)
            state.PursuitDisengageExtended = false;

        DB_RoomContext context = DB_RoomContext.For(bat.room);
        Player player = context != null
            ? NearestVisiblePlayer(bat, context.Players, 300f)
            : null;
        if (player == null)
        {
            state.PursuitTicks = 0;
            state.PursuitLastDistance = -1f;
            return;
        }

        int slot = PlayerSlot(player);
        state.EscapeThreatPlayerSlot = slot;
        float distance = Vector2.Distance(player.mainBodyChunk.pos, bat.mainBodyChunk.pos);
        float closing = Vector2.Dot(
            player.mainBodyChunk.vel,
            Custom.DirVec(player.mainBodyChunk.pos, bat.mainBodyChunk.pos));
        bool continuing = state.PursuitPlayerSlot == slot && state.PursuitLastDistance >= 0f &&
            distance < state.PursuitLastDistance - 0.35f && closing > 1.25f;
        if (continuing) state.PursuitTicks++;
        else if (closing > 1.6f) state.PursuitTicks = Mathf.Max(1, state.PursuitTicks - 1);
        else state.PursuitTicks = Mathf.Max(0, state.PursuitTicks - 3);
        state.PursuitPlayerSlot = slot;
        state.PursuitLastDistance = distance;

        if (!state.PursuitAwarded && state.PursuitTicks >= PursuitMinimumTicks)
        {
            AddEvidence(
                bat,
                player,
                DesertBatflyThreatAdapterRegistry.PursuitEvidence(),
                1f,
                "sustained pursuit",
                false);
            state.PursuitAwarded = true;
        }
    }

    private static void ExtendLearnedDisengage(DesertBatfly bat, RuntimeState state)
    {
        if (state.PreviousMode != DesertBatflyAI.Activity.Escape ||
            bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape ||
            state.PursuitDisengageExtended || !ValidSlot(state.EscapeThreatPlayerSlot))
            return;

        DB_PlayerThreatMemory memory = DB_ThreatMemoryStore.For(
            bat.DesertState, state.EscapeThreatPlayerSlot);
        if (memory == null || memory.PursuitPressure < 0.26f) return;

        Player player = DB_RoomContext.For(bat.room)?.PlayerBySlot(state.EscapeThreatPlayerSlot);
        if (player == null || !Custom.DistLess(
                bat.mainBodyChunk.pos,
                player.mainBodyChunk.pos,
                Mathf.Lerp(190f, 300f, memory.PursuitPressure)))
            return;

        state.PursuitDisengageExtended = true;
        DesertBatflySocialLife.CancelForPriority(bat, "Task11 learned pursuit disengage");
        bat.DesertAI.Threatened(player, false);
        state.ModifierReason = "learned pursuer: extended disengage";
    }

    private static void TrackEncounter(DesertBatfly bat, RuntimeState state)
    {
        if (bat.DesertAI.Target is not Player player || bat.room == null ||
            bat.DesertAI.Mode is DesertBatflyAI.Activity.Escape or DesertBatflyAI.Activity.Attach or
                DesertBatflyAI.Activity.Interfere or DesertBatflyAI.Activity.RetaliationCharge)
        {
            ResetEncounter(state);
            return;
        }

        int slot = PlayerSlot(player);
        if (!ValidSlot(slot) ||
            !DB_VisibilityPolicy.CanObserve(
                bat, player.mainBodyChunk.pos, 360f, DB_VisibilityChannel.Player))
        {
            ResetEncounter(state);
            return;
        }

        float distance = Vector2.Distance(bat.mainBodyChunk.pos, player.mainBodyChunk.pos);
        if (distance > 360f)
        {
            ResetEncounter(state);
            return;
        }

        if (state.EncounterPlayerSlot != slot)
        {
            ResetEncounter(state);
            state.EncounterPlayerSlot = slot;
            state.EncounterLastDistance = distance;
            return;
        }

        RoomState roomState = RoomFor(bat.room);
        int clock = bat.room.game?.clock ?? 0;
        bool playerRecentlyAttacked =
            Recent(roomState.RecentSpearThrow[slot], clock, 120) ||
            Recent(roomState.RecentRockThrow[slot], clock, 120) ||
            Recent(roomState.RecentExplosion[slot], clock, 160) ||
            Recent(roomState.RecentGrab[slot], clock, 120) ||
            (state.RecentDamagePlayerSlot == slot && Recent(state.RecentDamageTick, clock, 160));

        state.EncounterTicks++;
        if (!playerRecentlyAttacked && state.EncounterLastDistance >= 0f &&
            distance > state.EncounterLastDistance + 0.35f)
            state.RetreatTicks++;
        else
            state.RetreatTicks = Mathf.Max(0, state.RetreatTicks - 2);
        state.EncounterLastDistance = distance;

        if (!state.RetreatAwarded && state.RetreatTicks >= RetreatMinimumTicks)
        {
            AddEvidence(
                bat,
                player,
                DesertBatflyThreatAdapterRegistry.RetreatEvidence(),
                1f,
                "encounter retreat",
                false);
            state.RetreatAwarded = true;
        }

        if (!playerRecentlyAttacked &&
            state.EncounterTicks >= NonAggressionEncounterTicks * (state.NonAggressionAwards + 1) &&
            state.NonAggressionAwards < 3)
        {
            AddEvidence(
                bat,
                player,
                DesertBatflyThreatAdapterRegistry.NonAggressionEvidence(),
                1f,
                "non-aggressive encounter",
                false);
            state.NonAggressionAwards++;
        }
    }

    private static void ResetEncounter(RuntimeState state)
    {
        state.EncounterPlayerSlot = -1;
        state.EncounterTicks = 0;
        state.RetreatTicks = 0;
        state.EncounterLastDistance = -1f;
        state.RetreatAwarded = false;
        state.NonAggressionAwards = 0;
    }

    private static void ApplyTacticalAdjustment(DesertBatfly bat, RuntimeState state)
    {
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat)) return;

        state.ModifierReason = string.Empty;
        state.AttackGeometryAdjustment = string.Empty;
        state.AttachSuppression = 0f;
        state.EvadeTarget = null;
        if (bat.room == null || bat.dead || !bat.Consious || bat.inShortcut ||
            bat.Injury.IsSeverelyInjured || bat.AI.fleeFromRain)
            return;

        if (bat.DesertAI.Target is not Player player || player.room != bat.room) return;
        int slot = PlayerSlot(player);
        DB_PlayerThreatMemory memory =
            DB_ThreatMemoryStore.For(bat.DesertState, slot);
        if (memory == null || memory.Confidence <= 0.02f) return;

        DesertBatflyThreatCue cue = state.Cue.PlayerSlot == slot ? state.Cue : default;
        float nerve = bat.Personality.Nerve;
        float projectileRisk = Mathf.Clamp01(
            memory.ProjectilePressure * 0.35f + memory.PiercingPressure * 0.65f);
        float closeRisk = Mathf.Clamp01(
            memory.GrabCapturePressure * 0.55f + memory.ShockPressure * 0.25f +
            memory.BluntStunPressure * 0.20f);
        float explosionRisk = Mathf.Clamp01(
            memory.ExplosionPressure * 0.72f + memory.AreaDenialPressure * 0.28f);
        float counterRisk = memory.CounterKillPressure;
        float caution = Mathf.Clamp01(
            projectileRisk * 0.38f + closeRisk * 0.22f +
            explosionRisk * 0.22f + counterRisk * 0.18f);
        caution *= Mathf.Lerp(1.18f, 0.72f, nerve) *
                   Mathf.Lerp(0.72f, 1f, memory.Confidence);
        if (cue.VisibleSpear) caution += memory.PiercingPressure * 0.18f;
        if (cue.VisibleExplosive) caution += memory.ExplosionPressure * 0.18f;
        if (cue.VisibleStartle) caution += memory.StartlePressure * 0.10f;
        caution = Mathf.Clamp01(caution);

        float confidenceRelief = memory.NonAggressionConfidence * 0.10f +
            memory.RetreatTendency * bat.Personality.Temperament * bat.Personality.Nerve * 0.12f;
        caution = Mathf.Max(0f, caution - confidenceRelief);

        Vector2 playerCenter = player.mainBodyChunk.pos;
        Vector2 currentOffset = bat.AI.localGoal - playerCenter;
        if (currentOffset.sqrMagnitude < 4f)
            currentOffset = bat.mainBodyChunk.pos - playerCenter;
        if (currentOffset.sqrMagnitude < 4f)
            currentOffset = Vector2.right * StableSide(bat, slot);

        bool extremeVengeance = DesertBatflyIntimidation.IsExtremeVengeanceActive(bat);
        switch (bat.DesertAI.Mode)
        {
            case DesertBatflyAI.Activity.Observe:
            {
                if (!extremeVengeance && state.PreviousMode != DesertBatflyAI.Activity.Observe &&
                    ShouldAbandonFreshHarass(bat, slot, memory, caution))
                {
                    bat.DesertAI.CancelAttack();
                    Vector2 away = Custom.DirVec(playerCenter, bat.mainBodyChunk.pos);
                    DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, bat.mainBodyChunk.pos + away * 95f);
                    state.EvadeTarget = bat.AI.localGoal;
                    state.ModifierReason = "learned counter-kill caution";
                    state.AttackGeometryAdjustment = "new harassment attempt abandoned";
                    break;
                }

                float scale = 1f + caution * 0.48f +
                    (cue.VisibleSpear ? memory.PiercingPressure * 0.22f : 0f);
                Vector2 offset = currentOffset.normalized *
                    Mathf.Max(currentOffset.magnitude, 135f) * scale;
                offset.y *= 0.72f;
                DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, playerCenter + offset);
                state.ModifierReason = "learned ranged threat";
                state.AttackGeometryAdjustment = "observe radius increased";
                break;
            }

            case DesertBatflyAI.Activity.Approach:
            {
                float side = StableSide(bat, slot);
                float lateral = Mathf.Lerp(
                    20f,
                    85f,
                    Mathf.Clamp01(projectileRisk + counterRisk * 0.35f));
                Vector2 approachDir = Custom.DirVec(bat.mainBodyChunk.pos, playerCenter);
                Vector2 perpendicular = new Vector2(-approachDir.y, approachDir.x) * side;
                DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, playerCenter +
                    Vector2.up * Mathf.Lerp(92f, 125f, caution) + perpendicular * lateral);
                state.ModifierReason = "learned side approach";
                state.AttackGeometryAdjustment = "frontal approach reduced";
                break;
            }

            case DesertBatflyAI.Activity.Circle:
            {
                DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, playerCenter + currentOffset * (1f + caution * 0.55f));
                state.ModifierReason = "learned circle spacing";
                state.AttackGeometryAdjustment = "circle radius increased";
                break;
            }

            case DesertBatflyAI.Activity.FakeDive:
            {
                if (!bat.DesertAI.PullingUp && caution > 0.18f)
                {
                    float side = StableSide(bat, slot);
                    Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, playerCenter);
                    Vector2 perpendicular = new Vector2(-direction.y, direction.x) * side;
                    DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, bat.AI.localGoal + (perpendicular * Mathf.Lerp(12f, 48f, caution)));
                    state.ModifierReason = "learned feint geometry";
                    state.AttackGeometryAdjustment = "fake dive shifted laterally";
                }
                break;
            }

            case DesertBatflyAI.Activity.Dive:
            {
                float side = StableSide(bat, slot);
                Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, playerCenter);
                Vector2 perpendicular = new Vector2(-direction.y, direction.x) * side;
                DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, bat.AI.localGoal + (perpendicular * Mathf.Lerp(8f, 62f, caution)));
                state.ModifierReason = "learned dive geometry";
                state.AttackGeometryAdjustment = "straight dive reduced";
                if (!extremeVengeance && state.PreviousMode != DesertBatflyAI.Activity.Dive &&
                    ShouldAbortDive(bat, slot, caution, counterRisk, cue))
                {
                    bat.DesertAI.CancelAttack();
                    Vector2 evade = bat.mainBodyChunk.pos +
                        Custom.DirVec(playerCenter, bat.mainBodyChunk.pos) * 100f + perpendicular * 55f;
                    DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, evade);
                    state.EvadeTarget = evade;
                    state.ModifierReason = "learned attack abort";
                    state.AttackGeometryAdjustment = "dive aborted after threat assessment";
                }
                break;
            }

            case DesertBatflyAI.Activity.Attach:
            {
                state.AttachSuppression = Mathf.Clamp01(
                    closeRisk * 0.55f + counterRisk * 0.45f +
                    (cue.RecentGrabAttempt ? 0.18f : 0f));
                if (!extremeVengeance &&
                    state.AttachSuppression > Mathf.Lerp(0.88f, 0.58f, 1f - nerve) &&
                    Stable01(bat, slot, 0x53A9) < state.AttachSuppression * 0.55f)
                {
                    bat.DesertAI.CancelAttack();
                    Vector2 evade = bat.mainBodyChunk.pos +
                        Custom.DirVec(playerCenter, bat.mainBodyChunk.pos) * 110f;
                    DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, evade);
                    state.EvadeTarget = evade;
                    state.ModifierReason = "learned close-range capture risk";
                    state.AttackGeometryAdjustment = "attach aborted";
                }
                break;
            }

            case DesertBatflyAI.Activity.RetaliationCharge:
            {
                float side = StableSide(bat, slot);
                Vector2 direction = Custom.DirVec(bat.mainBodyChunk.pos, playerCenter);
                Vector2 perpendicular = new Vector2(-direction.y, direction.x) * side;
                DB_FlightMotor.TryRetarget(bat, DB_BehaviorOwner.Combat, bat.AI.localGoal + (perpendicular * Mathf.Lerp(6f, 38f, caution)));
                state.ModifierReason = "retaliation keeps learned geometry";
                state.AttackGeometryAdjustment = "retaliation charge shifted laterally";
                break;
            }
        }
    }

    private static bool ShouldAbandonFreshHarass(
        DesertBatfly bat,
        int slot,
        DB_PlayerThreatMemory memory,
        float caution)
    {
        float chance =
            memory.CounterKillPressure * 0.34f +
            memory.PursuitPressure * 0.10f +
            memory.PiercingPressure * 0.10f + caution * 0.10f;
        chance *= Mathf.Lerp(1.12f, 0.55f, bat.Personality.Nerve);
        chance *= Mathf.Lerp(1.08f, 0.52f, bat.Personality.Temperament);
        return Stable01(bat, slot, 0x6E21) < Mathf.Clamp01(chance);
    }

    private static bool ShouldAbortDive(
        DesertBatfly bat,
        int slot,
        float caution,
        float counterRisk,
        in DesertBatflyThreatCue cue)
    {
        float chance = caution * 0.30f + counterRisk * 0.18f;
        if (cue.VisibleSpear) chance += 0.12f;
        if (cue.ProjectileThreat) chance += 0.28f;
        chance *= Mathf.Lerp(1.12f, 0.58f, bat.Personality.Nerve);
        chance *= Mathf.Lerp(1.05f, 0.70f, bat.Personality.Temperament);
        return Stable01(bat, slot, 0x19C7) < Mathf.Clamp01(chance);
    }

    private static void TickAcute(RuntimeState state)
    {
        if (state.AcuteExplosionTimer > 0) state.AcuteExplosionTimer--;
        if (state.AcuteStartleTimer > 0) state.AcuteStartleTimer--;
        if (state.AcuteMassCasualtyTimer > 0) state.AcuteMassCasualtyTimer--;
        if (state.AcuteCaptureTimer > 0) state.AcuteCaptureTimer--;
        if (state.AcuteShockTimer > 0) state.AcuteShockTimer--;
        if (state.HazardTimer > 0)
        {
            state.HazardTimer--;
            if (state.HazardTimer == 0) state.HazardCenter = null;
        }
        if (state.AcuteExplosionTimer <= 0 && state.AcuteStartleTimer <= 0 &&
            state.AcuteMassCasualtyTimer <= 0 && state.AcuteCaptureTimer <= 0 &&
            state.AcuteShockTimer <= 0)
            state.AcuteInstigator = null;
    }

    private static void RememberSourceOwner(PhysicalObject source, Player player, int clock)
    {
        if (source == null || player == null) return;
        SourceOwner owner = sourceOwners.GetValue(source, _ => new SourceOwner());
        owner.Player = player;
        owner.Clock = clock;
    }

    private static Player ResolvePlayer(PhysicalObject source, Creature killTagHolder)
    {
        if (killTagHolder is Player direct) return direct;
        if (source is Player playerSource) return playerSource;
        if (source is Weapon weapon && weapon.thrownBy is Player thrown) return thrown;
        if (source?.grabbedBy != null)
        {
            for (int i = 0; i < source.grabbedBy.Count; i++)
                if (source.grabbedBy[i]?.grabber is Player holder) return holder;
        }
        if (source != null && sourceOwners.TryGetValue(source, out SourceOwner remembered))
            return remembered.Player;
        return null;
    }

    private static Player NearestVisiblePlayer(
        DesertBatfly bat,
        IReadOnlyList<Player> players,
        float maxDistance = 430f)
    {
        if (bat == null || players == null) return null;
        Player best = null;
        float bestDistance = maxDistance;
        for (int i = 0; i < players.Count; i++)
        {
            Player player = players[i];
            if (player == null || player.dead || player.room != bat.room) continue;
            float distance = Vector2.Distance(bat.mainBodyChunk.pos, player.mainBodyChunk.pos);
            if (distance >= bestDistance ||
                !DB_VisibilityPolicy.CanObserve(
                    bat, player.mainBodyChunk.pos, maxDistance, DB_VisibilityChannel.Player))
                continue;
            bestDistance = distance;
            best = player;
        }
        return best;
    }

    private static RuntimeState StateFor(DesertBatfly bat) => states.GetOrCreateValue(bat);
    private static RoomState RoomFor(Room room) => roomStates.GetOrCreateValue(room);

    private static int CurrentCycle(DesertBatfly bat)
    {
        RainWorldGame game = bat?.room?.game;
        return game != null && game.IsStorySession
            ? game.GetStorySession.saveState.cycleNumber
            : -1;
    }

    internal static int PlayerSlot(Player player) => player?.playerState?.playerNumber ?? -1;
    internal static bool ValidSlot(int slot) =>
        slot >= 0 && slot < DB_ThreatMemorySet.PlayerSlots;

    private static bool Recent(int stamp, int clock, int window) =>
        stamp != int.MinValue && clock >= stamp && clock - stamp <= window;

    private static int WeaponIdentity(Weapon weapon)
    {
        if (weapon?.abstractPhysicalObject == null) return RuntimeHelpers.GetHashCode(weapon);
        unchecked
        {
            EntityID id = weapon.abstractPhysicalObject.ID;
            return id.spawner * 397 ^ id.number;
        }
    }

    private static float StableSide(DesertBatfly bat, int slot) =>
        Stable01(bat, slot, 0x2C15) < 0.5f ? -1f : 1f;

    private static float Stable01(DesertBatfly bat, int slot, int salt)
    {
        unchecked
        {
            uint x = (uint)(
                bat.Personality.VisualSeed * 1103515245 +
                slot * 486187739 + salt * 12345);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }
}