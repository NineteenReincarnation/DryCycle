using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Authoritative semantic-event root for one Desert Batfly life.
///
/// This type observes Rain World facts only. It does not choose behavior, write velocity,
/// mutate persistent CreatureState, or know Task-number architecture. Domain systems
/// subscribe to Damage/Capture/Mortality and remain responsible for their own reactions.
/// </summary>
internal static class DB_EventHub
{
    internal const int MortalityAttributionTicks = 260;
    internal const int ConsumptionAttributionTicks = 8;

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
        internal Creature.DamageType LastDamageType;
        internal float LastDamageAmount;
        internal float LastDamageStun;
        internal int LastDamageClock = int.MinValue;
        internal float LastThreatScale;

        internal Player ExplicitConsumer;
        internal int ExplicitConsumeClock = int.MinValue;

        internal readonly CaptureSession Capture = new();
        internal int ViolenceDepth;
        internal DB_MortalityEvent? PendingMortality;
        internal bool MortalityPublished;

        internal bool TryMarkMortality()
        {
            if (MortalityPublished) return false;
            MortalityPublished = true;
            return true;
        }
    }

    private static ConditionalWeakTable<DesertBatfly, VictimState> victims = new();
    private static bool enabled;
    private static int nextSequence;

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

        // The species lifecycle disables all consumers as well, but clearing delegates here
        // guarantees a stale subscriber can never survive a full EventHub disable/re-enable.
        Damage = null;
        Capture = null;
        Mortality = null;
    }

    internal static void ResetState()
    {
        victims = new ConditionalWeakTable<DesertBatfly, VictimState>();
        nextSequence = 0;
    }

    /// <summary>
    /// Records the explicit lethal action used by vanilla Fly.BitByPlayer. A grasp alone is
    /// never mortality attribution: drowning/environmental death while held stays
    /// unattributed unless a real damage or consume fact exists.
    /// </summary>
    internal static void RecordConsumptionAttribution(DesertBatfly victim, Player consumer)
    {
        if (victim == null || consumer == null || victim.dead || victim.slatedForDeletetion)
            return;
        VictimState state = StateFor(victim);
        state.ExplicitConsumer = consumer;
        state.ExplicitConsumeClock = Clock(victim);
    }

    internal static bool WithinAttributionWindow(int now, int then, int window)
    {
        return now != int.MinValue && then != int.MinValue && window >= 0 &&
               now >= then && now - then <= window;
    }

    private static int NextSequence()
    {
        nextSequence = nextSequence == int.MaxValue ? 1 : nextSequence + 1;
        return nextSequence;
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

        VictimState state = StateFor(victim);
        bool wasDead = victim.dead;
        PhysicalObject sourceObject = source?.owner;
        Creature instigator = ResolveInstigator(sourceObject);
        int clock = Clock(victim);
        int sequence = NextSequence();
        Vector2 position = victim.mainBodyChunk?.pos ?? Vector2.zero;
        bool canAttributeMortality =
            instigator != null && damage > 0f && sourceObject is not Rock;
        float threatScale = ThreatScale(instigator, sourceObject);

        // Attribution facts must exist before vanilla damage runs because Creature.Violence
        // may call Die() re-entrantly. The semantic Damage event itself is delayed until
        // vanilla finishes so subscribers know whether this hit was actually lethal.
        if (canAttributeMortality)
        {
            state.LastDamageInstigator = instigator;
            state.LastDamageSource = sourceObject;
            state.LastDamageType = type;
            state.LastDamageAmount = Mathf.Max(0f, damage);
            state.LastDamageStun = Mathf.Max(0f, stunBonus);
            state.LastDamageClock = clock;
            state.LastThreatScale = threatScale;
        }

        state.ViolenceDepth++;
        try
        {
            orig(self, source, momentum, hitChunk, appendage, type, damage, stunBonus);
        }
        finally
        {
            state.ViolenceDepth = Mathf.Max(0, state.ViolenceDepth - 1);
        }

        bool lethal = !wasDead && victim.dead;
        Dispatch(Damage, new DB_DamageEvent(
            sequence,
            victim,
            instigator,
            sourceObject,
            type,
            damage,
            stunBonus,
            position,
            clock,
            canAttributeMortality,
            lethal));

        // A Die() reached from inside Creature.Violence is buffered until after Damage is
        // delivered. This gives every consumer one causal order: Damage -> Mortality.
        FlushPendingMortality(state);
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
            state,
            clock,
            out Creature killer,
            out PhysicalObject sourceObject,
            out Creature.DamageType damageType,
            out float damage,
            out float stun,
            out float threatScale,
            out bool wasConsumed,
            out DB_MortalityAttribution attribution);

        orig(self);

        if (wasDead || !victim.dead || !state.TryMarkMortality()) return;

        var mortality = new DB_MortalityEvent(
            NextSequence(),
            victim,
            killer,
            sourceObject,
            damageType,
            damage,
            stun,
            deathPosition,
            chainWitnesses,
            threatScale,
            revengeFailed,
            wasConsumed,
            clock,
            attribution);

        if (state.ViolenceDepth > 0)
            state.PendingMortality = mortality;
        else
            Dispatch(Mortality, mortality);
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
            NextSequence(),
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
        VictimState state,
        int clock,
        out Creature killer,
        out PhysicalObject sourceObject,
        out Creature.DamageType damageType,
        out float damage,
        out float stun,
        out float threatScale,
        out bool wasConsumed,
        out DB_MortalityAttribution attribution)
    {
        killer = null;
        sourceObject = null;
        damageType = null;
        damage = 0f;
        stun = 0f;
        threatScale = 0.82f;
        wasConsumed = false;
        attribution = DB_MortalityAttribution.Unattributed;

        if (state?.ExplicitConsumer != null &&
            WithinAttributionWindow(clock, state.ExplicitConsumeClock, ConsumptionAttributionTicks))
        {
            killer = state.ExplicitConsumer;
            sourceObject = state.ExplicitConsumer;
            threatScale = 0.90f;
            wasConsumed = true;
            attribution = DB_MortalityAttribution.ExplicitConsumption;
            return;
        }

        if (state != null && state.LastDamageInstigator != null &&
            WithinAttributionWindow(clock, state.LastDamageClock, MortalityAttributionTicks))
        {
            killer = state.LastDamageInstigator;
            sourceObject = state.LastDamageSource;
            damageType = state.LastDamageType;
            damage = state.LastDamageAmount;
            stun = state.LastDamageStun;
            threatScale = state.LastThreatScale > 0f
                ? state.LastThreatScale
                : ThreatScale(killer, sourceObject);
            attribution = DB_MortalityAttribution.RecentDamage;
        }
    }

    private static void FlushPendingMortality(VictimState state)
    {
        if (state == null || state.ViolenceDepth > 0 || !state.PendingMortality.HasValue)
            return;
        DB_MortalityEvent mortality = state.PendingMortality.Value;
        state.PendingMortality = null;
        Dispatch(Mortality, mortality);
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
    ExplicitConsumption
}

internal readonly struct DB_DamageEvent
{
    internal readonly DB_EventKind Kind;
    internal readonly int Sequence;
    internal readonly DesertBatfly Victim;
    internal readonly Creature Instigator;
    internal readonly PhysicalObject SourceObject;
    internal readonly Creature.DamageType DamageType;
    internal readonly float Damage;
    internal readonly float Stun;
    internal readonly Vector2 Position;
    internal readonly int Clock;
    internal readonly bool CanAttributeMortality;
    internal readonly bool Lethal;

    internal DB_DamageEvent(
        int sequence,
        DesertBatfly victim,
        Creature instigator,
        PhysicalObject sourceObject,
        Creature.DamageType damageType,
        float damage,
        float stun,
        Vector2 position,
        int clock,
        bool canAttributeMortality,
        bool lethal)
    {
        Kind = DB_EventKind.Damage;
        Sequence = Mathf.Max(1, sequence);
        Victim = victim;
        Instigator = instigator;
        SourceObject = sourceObject;
        DamageType = damageType;
        Damage = Mathf.Max(0f, damage);
        Stun = Mathf.Max(0f, stun);
        Position = position;
        Clock = clock;
        CanAttributeMortality = canAttributeMortality;
        Lethal = lethal;
    }
}

internal readonly struct DB_CaptureEvent
{
    internal readonly DB_EventKind Kind;
    internal readonly int Sequence;
    internal readonly DesertBatfly Victim;
    internal readonly Creature Captor;
    internal readonly LizardTongue Tongue;
    internal readonly DB_CaptureKind CaptureKind;
    internal readonly Vector2 Position;
    internal readonly int Clock;
    internal readonly int SessionSerial;

    internal DB_CaptureEvent(
        int sequence,
        DesertBatfly victim,
        Creature captor,
        LizardTongue tongue,
        DB_CaptureKind captureKind,
        Vector2 position,
        int clock,
        int sessionSerial)
    {
        Kind = DB_EventKind.Capture;
        Sequence = Mathf.Max(1, sequence);
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
    internal readonly int Sequence;
    internal readonly DesertBatfly Victim;
    internal readonly Creature Killer;
    internal readonly PhysicalObject SourceObject;
    internal readonly Creature.DamageType DamageType;
    internal readonly float Damage;
    internal readonly float Stun;
    internal readonly Vector2 Position;
    internal readonly DesertBatfly[] ChainWitnesses;
    internal readonly float ThreatScale;
    internal readonly bool RevengeFailed;
    internal readonly bool WasConsumed;
    internal readonly int Clock;
    internal readonly DB_MortalityAttribution Attribution;

    internal DB_MortalityEvent(
        int sequence,
        DesertBatfly victim,
        Creature killer,
        PhysicalObject sourceObject,
        Creature.DamageType damageType,
        float damage,
        float stun,
        Vector2 position,
        DesertBatfly[] chainWitnesses,
        float threatScale,
        bool revengeFailed,
        bool wasConsumed,
        int clock,
        DB_MortalityAttribution attribution)
    {
        Kind = DB_EventKind.Mortality;
        Sequence = Mathf.Max(1, sequence);
        Victim = victim;
        Killer = killer;
        SourceObject = sourceObject;
        DamageType = damageType;
        Damage = Mathf.Max(0f, damage);
        Stun = Mathf.Max(0f, stun);
        Position = position;
        ChainWitnesses = chainWitnesses ?? Array.Empty<DesertBatfly>();
        ThreatScale = Mathf.Max(0f, threatScale);
        RevengeFailed = revengeFailed;
        WasConsumed = wasConsumed;
        Clock = clock;
        Attribution = attribution;
    }
}