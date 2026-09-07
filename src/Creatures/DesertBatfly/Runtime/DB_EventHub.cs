using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Authoritative semantic-event root for one Desert Batfly life.
///
/// This type observes Rain World facts only. It does not choose behavior, write velocity,
/// mutate persistent state, or know Task-number architecture. Domain systems subscribe to
/// Damage/Capture/Mortality and remain responsible for their own reactions.
/// </summary>
internal static class DB_EventHub
{
    internal const int MortalityAttributionTicks = 260;

    internal static event Action<DB_DamageEvent> Damage;
    internal static event Action<DB_CaptureEvent> Capture;
    internal static event Action<DB_MortalityEvent> Mortality;

    private sealed class CaptureSession
    {
        internal object Captor;
        internal LizardTongue Tongue;
        internal DB_CaptureKind Kind;
        internal int Serial;
        internal bool Active;

        // priorStillActive is sampled before a new grasp is installed. That distinction
        // lets tongue -> bite/grasp transfer stay one capture while a true later recapture
        // starts a new semantic event.
        internal bool Accept(object captor, bool priorStillActive)
        {
            if (captor == null) return false;
            if (Active && ReferenceEquals(Captor, captor) && priorStillActive)
                return false;

            Captor = captor;
            Active = true;
            Serial++;
            return true;
        }
    }

    private sealed class VictimState
    {
        internal Creature LastDamageInstigator;
        internal PhysicalObject LastDamageSource;
        internal int LastDamageClock = int.MinValue;
        internal float LastThreatScale;
        internal readonly CaptureSession Capture = new();
        internal bool MortalityPublished;

        internal bool TryMarkMortality()
        {
            if (MortalityPublished) return false;
            MortalityPublished = true;
            return true;
        }
    }

    private sealed class TongueObservation
    {
        internal LizardTongue.State State;
        internal PhysicalObject Owner;
    }

    private static ConditionalWeakTable<DesertBatfly, VictimState> victims = new();
    private static ConditionalWeakTable<LizardTongue, TongueObservation> tongues = new();
    private static bool enabled;

    internal static bool Enabled => enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        ResetState();
        On.Creature.Violence += CreatureViolence;
        On.Creature.Die += CreatureDie;
        On.Fly.Grabbed += FlyGrabbed;
        On.LizardTongue.Update += TongueUpdate;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.Creature.Violence -= CreatureViolence;
        On.Creature.Die -= CreatureDie;
        On.Fly.Grabbed -= FlyGrabbed;
        On.LizardTongue.Update -= TongueUpdate;
        ResetState();
    }

    internal static void ResetState()
    {
        victims = new ConditionalWeakTable<DesertBatfly, VictimState>();
        tongues = new ConditionalWeakTable<LizardTongue, TongueObservation>();
    }

    internal static bool WithinAttributionWindow(int now, int then, int window)
    {
        return now != int.MinValue && then != int.MinValue && window >= 0 &&
               now >= then && now - then <= window;
    }

    private static void CreatureViolence(
        On.Creature.orig_Violence orig,
        Creature self,
        BodyChunk source,
        Vector2? momentum,
        BodyChunk hitChunk,
        Appendage.Pos appendage,
        Creature.DamageType type,
        float damage,
        float stunBonus)
    {
        if (self is not DesertBatfly victim)
        {
            orig(self, source, momentum, hitChunk, appendage, type, damage, stunBonus);
            return;
        }

        PhysicalObject sourceObject = source?.owner;
        Creature instigator = ResolveInstigator(sourceObject);
        int clock = Clock(victim);
        Vector2 position = victim.mainBodyChunk?.pos ?? Vector2.zero;
        bool canAttributeMortality =
            instigator != null && damage > 0f && sourceObject is not Rock;
        float threatScale = ThreatScale(instigator, sourceObject);

        VictimState state = StateFor(victim);
        if (canAttributeMortality)
        {
            state.LastDamageInstigator = instigator;
            state.LastDamageSource = sourceObject;
            state.LastDamageClock = clock;
            state.LastThreatScale = threatScale;
        }

        Dispatch(Damage, new DB_DamageEvent(
            victim,
            instigator,
            sourceObject,
            type,
            damage,
            stunBonus,
            position,
            clock,
            canAttributeMortality));

        orig(self, source, momentum, hitChunk, appendage, type, damage, stunBonus);
    }

    private static void CreatureDie(On.Creature.orig_Die orig, Creature self)
    {
        if (self is not DesertBatfly victim)
        {
            orig(self);
            return;
        }

        bool wasDead = victim.dead;
        VictimState state = StateFor(victim);
        int clock = Clock(victim);
        Vector2 deathPosition = victim.mainBodyChunk?.pos ?? Vector2.zero;
        DesertBatfly[] chainWitnesses = !wasDead
            ? DesertBatflyIntimidation.SnapshotChainWitnesses(victim)
            : Array.Empty<DesertBatfly>();
        bool revengeFailed = !wasDead &&
            DesertBatflyIntimidation.IsExtremeVengeanceActive(victim);

        ResolveMortalityAttribution(
            victim,
            state,
            clock,
            out Creature killer,
            out PhysicalObject sourceObject,
            out float threatScale,
            out DB_MortalityAttribution attribution);

        orig(self);

        if (wasDead || !victim.dead || !state.TryMarkMortality()) return;

        Dispatch(Mortality, new DB_MortalityEvent(
            victim,
            killer,
            sourceObject,
            deathPosition,
            chainWitnesses,
            threatScale,
            revengeFailed,
            clock,
            attribution));
    }

    private static void FlyGrabbed(On.Fly.orig_Grabbed orig, Fly self, Creature.Grasp grasp)
    {
        if (self is DesertBatfly victim && !victim.dead &&
            grasp?.grabber is Creature captor && captor is not Fly)
        {
            // Publish before orig installs this new grasp into grabbedBy. If a Peach tongue
            // is already holding the same victim, the existing tongue session remains
            // physically active and suppresses the transfer duplicate. A genuine recapture
            // after release has no prior active hold and therefore publishes again.
            ReportCapture(victim, captor, null, DB_CaptureKind.Grasp);
        }

        orig(self, grasp);
    }

    private static void TongueUpdate(On.LizardTongue.orig_Update orig, LizardTongue self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        TongueObservation observation = tongues.GetOrCreateValue(self);
        LizardTongue.State beforeState = self.state;
        PhysicalObject beforeOwner = self.attached?.owner;

        orig(self);

        LizardTongue.State afterState = self.state;
        PhysicalObject afterOwner = self.attached?.owner;
        bool enteredCapture =
            afterState == LizardTongue.State.AttachedInSmallObject &&
            afterOwner is DesertBatfly victim && !victim.dead &&
            self.lizard != null &&
            (beforeState != LizardTongue.State.AttachedInSmallObject ||
             !ReferenceEquals(beforeOwner, afterOwner));

        observation.State = afterState;
        observation.Owner = afterOwner;

        if (enteredCapture)
            ReportCapture(victim, self.lizard, self, DB_CaptureKind.Tongue);
    }

    private static void ReportCapture(
        DesertBatfly victim,
        Creature captor,
        LizardTongue tongue,
        DB_CaptureKind kind)
    {
        if (victim == null || captor == null || victim.dead || victim.slatedForDeletetion)
            return;

        VictimState state = StateFor(victim);
        bool priorStillActive = CaptureStillActive(victim, state.Capture);
        if (!state.Capture.Accept(captor, priorStillActive)) return;

        state.Capture.Tongue = tongue;
        state.Capture.Kind = kind;

        Dispatch(Capture, new DB_CaptureEvent(
            victim,
            captor,
            tongue,
            kind,
            victim.mainBodyChunk?.pos ?? Vector2.zero,
            Clock(victim),
            state.Capture.Serial));
    }

    private static bool CaptureStillActive(DesertBatfly victim, CaptureSession session)
    {
        if (victim == null || session == null || !session.Active || session.Captor is not Creature captor)
            return false;

        if (session.Tongue != null &&
            session.Tongue.state == LizardTongue.State.AttachedInSmallObject &&
            session.Tongue.attached?.owner == victim &&
            ReferenceEquals(session.Tongue.lizard, captor))
            return true;

        if (victim.grabbedBy != null)
        {
            for (int i = 0; i < victim.grabbedBy.Count; i++)
            {
                if (ReferenceEquals(victim.grabbedBy[i]?.grabber, captor))
                    return true;
            }
        }

        session.Active = false;
        session.Tongue = null;
        return false;
    }

    private static void ResolveMortalityAttribution(
        DesertBatfly victim,
        VictimState state,
        int clock,
        out Creature killer,
        out PhysicalObject sourceObject,
        out float threatScale,
        out DB_MortalityAttribution attribution)
    {
        killer = null;
        sourceObject = null;
        threatScale = 0.82f;
        attribution = DB_MortalityAttribution.Unattributed;

        if (state != null && state.LastDamageInstigator != null &&
            WithinAttributionWindow(clock, state.LastDamageClock, MortalityAttributionTicks))
        {
            killer = state.LastDamageInstigator;
            sourceObject = state.LastDamageSource;
            threatScale = state.LastThreatScale > 0f
                ? state.LastThreatScale
                : ThreatScale(killer, sourceObject);
            attribution = DB_MortalityAttribution.RecentDamage;
            return;
        }

        if (state?.Capture?.Captor is Creature captureOwner &&
            CaptureStillActive(victim, state.Capture))
        {
            killer = captureOwner;
            threatScale = captureOwner is Player ? 0.90f : ThreatScale(captureOwner, null);
            attribution = DB_MortalityAttribution.ActiveCapture;
            return;
        }

        if (victim?.grabbedBy == null) return;
        for (int i = 0; i < victim.grabbedBy.Count; i++)
        {
            Creature grabber = victim.grabbedBy[i]?.grabber;
            if (grabber == null || grabber is Fly) continue;
            killer = grabber;
            threatScale = grabber is Player ? 0.90f : ThreatScale(grabber, null);
            attribution = DB_MortalityAttribution.ActiveCapture;
            if (grabber is Lizard) return;
        }
    }

    private static Creature ResolveInstigator(PhysicalObject sourceObject)
    {
        if (sourceObject is Weapon weapon && weapon.thrownBy != null)
            return weapon.thrownBy;
        return sourceObject as Creature;
    }

    private static float ThreatScale(Creature instigator, PhysicalObject sourceObject)
    {
        if (instigator is Player)
            return sourceObject is Spear ? 1f : 0.82f;
        if (instigator is Lizard) return 1.05f;
        return instigator != null ? 0.90f : 0.82f;
    }

    private static int Clock(DesertBatfly victim) =>
        victim?.room?.game?.clock ?? int.MinValue;

    private static VictimState StateFor(DesertBatfly victim) =>
        victims.GetOrCreateValue(victim);

    private static void Dispatch<T>(Action<T> handlers, T semanticEvent)
    {
        if (handlers == null) return;
        Delegate[] invocation = handlers.GetInvocationList();
        for (int i = 0; i < invocation.Length; i++)
        {
            try { ((Action<T>)invocation[i])(semanticEvent); }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(
                    "[DryCycle/DesertBatfly] semantic event consumer failed: " + ex);
            }
        }
    }
}

internal enum DB_EventKind
{
    Damage,
    Capture,
    Mortality
}

internal enum DB_CaptureKind
{
    Grasp,
    Tongue
}

internal enum DB_MortalityAttribution
{
    Unattributed,
    RecentDamage,
    ActiveCapture
}

internal readonly struct DB_DamageEvent
{
    internal readonly DB_EventKind Kind;
    internal readonly DesertBatfly Victim;
    internal readonly Creature Instigator;
    internal readonly PhysicalObject SourceObject;
    internal readonly Creature.DamageType DamageType;
    internal readonly float Damage;
    internal readonly float Stun;
    internal readonly Vector2 Position;
    internal readonly int Clock;
    internal readonly bool CanAttributeMortality;

    internal DB_DamageEvent(
        DesertBatfly victim,
        Creature instigator,
        PhysicalObject sourceObject,
        Creature.DamageType damageType,
        float damage,
        float stun,
        Vector2 position,
        int clock,
        bool canAttributeMortality)
    {
        Kind = DB_EventKind.Damage;
        Victim = victim;
        Instigator = instigator;
        SourceObject = sourceObject;
        DamageType = damageType;
        Damage = Mathf.Max(0f, damage);
        Stun = Mathf.Max(0f, stun);
        Position = position;
        Clock = clock;
        CanAttributeMortality = canAttributeMortality;
    }
}

internal readonly struct DB_CaptureEvent
{
    internal readonly DB_EventKind Kind;
    internal readonly DesertBatfly Victim;
    internal readonly Creature Captor;
    internal readonly LizardTongue Tongue;
    internal readonly DB_CaptureKind CaptureKind;
    internal readonly Vector2 Position;
    internal readonly int Clock;
    internal readonly int SessionSerial;

    internal DB_CaptureEvent(
        DesertBatfly victim,
        Creature captor,
        LizardTongue tongue,
        DB_CaptureKind captureKind,
        Vector2 position,
        int clock,
        int sessionSerial)
    {
        Kind = DB_EventKind.Capture;
        Victim = victim;
        Captor = captor;
        Tongue = tongue;
        CaptureKind = captureKind;
        Position = position;
        Clock = clock;
        SessionSerial = Mathf.Max(1, sessionSerial);
    }
}

internal readonly struct DB_MortalityEvent
{
    internal readonly DB_EventKind Kind;
    internal readonly DesertBatfly Victim;
    internal readonly Creature Killer;
    internal readonly PhysicalObject SourceObject;
    internal readonly Vector2 Position;
    internal readonly DesertBatfly[] ChainWitnesses;
    internal readonly float ThreatScale;
    internal readonly bool RevengeFailed;
    internal readonly int Clock;
    internal readonly DB_MortalityAttribution Attribution;

    internal DB_MortalityEvent(
        DesertBatfly victim,
        Creature killer,
        PhysicalObject sourceObject,
        Vector2 position,
        DesertBatfly[] chainWitnesses,
        float threatScale,
        bool revengeFailed,
        int clock,
        DB_MortalityAttribution attribution)
    {
        Kind = DB_EventKind.Mortality;
        Victim = victim;
        Killer = killer;
        SourceObject = sourceObject;
        Position = position;
        ChainWitnesses = chainWitnesses ?? Array.Empty<DesertBatfly>();
        ThreatScale = Mathf.Max(0f, threatScale);
        RevengeFailed = revengeFailed;
        Clock = clock;
        Attribution = attribution;
    }
}
