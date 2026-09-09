using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Authoritative semantic-event root for one Desert Batfly life.
///
/// DB_Creature reports lifecycle facts it owns directly; only genuinely external facts such
/// as LizardTongue attachment are observed through Rain World hooks. This type does not choose
/// behavior, write velocity, or mutate persistent CreatureState. Domain systems subscribe to
/// Damage/Capture/Mortality and remain responsible for their own reactions.
/// </summary>
internal static class DB_EventHub
{
    internal const int MortalityAttributionTicks = 260;
    internal const int ConsumptionAttributionTicks = 8;

    internal static event Action<DB_DamageEvent> Damage;
    internal static event Action<DB_CaptureEvent> Capture;
    internal static event Action<DB_MortalityEvent> Mortality;

    internal sealed class CaptureSession
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

    internal sealed class VictimState
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

    /// <summary>
    /// Opaque transaction opened immediately before DB_Creature calls base.Violence.
    /// EndViolence must be called exactly once so nested Die() can keep Damage -> Mortality
    /// causal ordering without a global On.Creature.Violence detour.
    /// </summary>
    internal readonly struct ViolenceTransaction
    {
        internal readonly VictimState state;
        internal readonly DB_Creature victim;
        internal readonly bool wasDead;
        internal readonly int sequence;
        internal readonly Creature instigator;
        internal readonly PhysicalObject sourceObject;
        internal readonly Creature.DamageType damageType;
        internal readonly float damage;
        internal readonly float stun;
        internal readonly Vector2 position;
        internal readonly int clock;
        internal readonly bool canAttributeMortality;

        internal ViolenceTransaction(
            VictimState state,
            DB_Creature victim,
            bool wasDead,
            int sequence,
            Creature instigator,
            PhysicalObject sourceObject,
            Creature.DamageType damageType,
            float damage,
            float stun,
            Vector2 position,
            int clock,
            bool canAttributeMortality)
        {
            this.state = state;
            this.victim = victim;
            this.wasDead = wasDead;
            this.sequence = sequence;
            this.instigator = instigator;
            this.sourceObject = sourceObject;
            this.damageType = damageType;
            this.damage = damage;
            this.stun = stun;
            this.position = position;
            this.clock = clock;
            this.canAttributeMortality = canAttributeMortality;
        }

        internal bool Active => state != null && victim != null;
    }

    /// <summary>
    /// Opaque mortality snapshot captured after DB_Creature's Die guards accept the death but
    /// before base.Die mutates chain/grasp state.
    /// </summary>
    internal readonly struct MortalityTransaction
    {
        internal readonly VictimState state;
        internal readonly DB_Creature victim;
        internal readonly bool wasDead;
        internal readonly int clock;
        internal readonly Vector2 deathPosition;
        internal readonly DB_Creature[] chainWitnesses;
        internal readonly bool revengeFailed;
        internal readonly Creature killer;
        internal readonly PhysicalObject sourceObject;
        internal readonly Creature.DamageType damageType;
        internal readonly float damage;
        internal readonly float stun;
        internal readonly float threatScale;
        internal readonly bool wasConsumed;
        internal readonly DB_MortalityAttribution attribution;

        internal MortalityTransaction(
            VictimState state,
            DB_Creature victim,
            bool wasDead,
            int clock,
            Vector2 deathPosition,
            DB_Creature[] chainWitnesses,
            bool revengeFailed,
            Creature killer,
            PhysicalObject sourceObject,
            Creature.DamageType damageType,
            float damage,
            float stun,
            float threatScale,
            bool wasConsumed,
            DB_MortalityAttribution attribution)
        {
            this.state = state;
            this.victim = victim;
            this.wasDead = wasDead;
            this.clock = clock;
            this.deathPosition = deathPosition;
            this.chainWitnesses = chainWitnesses;
            this.revengeFailed = revengeFailed;
            this.killer = killer;
            this.sourceObject = sourceObject;
            this.damageType = damageType;
            this.damage = damage;
            this.stun = stun;
            this.threatScale = threatScale;
            this.wasConsumed = wasConsumed;
            this.attribution = attribution;
        }

        internal bool Active => state != null && victim != null;
    }

    private static ConditionalWeakTable<DB_Creature, VictimState> victims = new();
    private static bool enabled;
    private static int nextSequence;

    internal static bool Enabled => enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        ResetState();
        On.LizardTongue.Update += TongueUpdate;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
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
        victims = new ConditionalWeakTable<DB_Creature, VictimState>();
        nextSequence = 0;
    }

    /// <summary>
    /// Records the explicit lethal action used by vanilla Fly.BitByPlayer. A grasp alone is
    /// never mortality attribution: drowning/environmental death while held stays
    /// unattributed unless a real damage or consume fact exists.
    /// </summary>
    internal static void RecordConsumptionAttribution(DB_Creature victim, Player consumer)
    {
        if (!enabled || victim == null || consumer == null || victim.dead || victim.slatedForDeletetion)
            return;
        VictimState state = StateFor(victim);
        state.ExplicitConsumer = consumer;
        state.ExplicitConsumeClock = Clock(victim);
    }

    /// <summary>
    /// Reports a DB_Creature grasp before Fly.Grabbed installs the new grasp into grabbedBy.
    /// Keeping this pre-base boundary preserves tongue -> grasp transfer deduplication while
    /// avoiding a global On.Fly.Grabbed observer for our own virtual lifecycle.
    /// </summary>
    internal static void ReportGraspCapture(DB_Creature victim, Creature.Grasp grasp)
    {
        if (!enabled || victim == null || victim.dead || victim.slatedForDeletetion ||
            grasp?.grabber is not Creature captor || captor is Fly)
            return;
        ReportCapture(victim, captor, null, DB_CaptureKind.Grasp);
    }

    /// <summary>
    /// Opens the canonical damage transaction before vanilla Creature.Violence runs. Killer
    /// attribution is written here because vanilla may call virtual Die() re-entrantly.
    /// </summary>
    internal static ViolenceTransaction BeginViolence(
        DB_Creature victim,
        BodyChunk source,
        Creature.DamageType type,
        float damage,
        float stunBonus)
    {
        if (!enabled || victim == null)
            return default;

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
        return new ViolenceTransaction(
            state,
            victim,
            wasDead,
            sequence,
            instigator,
            sourceObject,
            type,
            damage,
            stunBonus,
            position,
            clock,
            canAttributeMortality);
    }

    /// <summary>
    /// Closes the damage transaction. The caller passes vanillaCompleted=false when
    /// base.Violence throws; depth is still restored but no semantic Damage is fabricated.
    /// </summary>
    internal static void EndViolence(in ViolenceTransaction transaction, bool vanillaCompleted)
    {
        if (!transaction.Active) return;

        transaction.state.ViolenceDepth = Mathf.Max(0, transaction.state.ViolenceDepth - 1);
        if (!vanillaCompleted) return;

        bool lethal = !transaction.wasDead && transaction.victim.dead;
        Dispatch(Damage, new DB_DamageEvent(
            transaction.sequence,
            transaction.victim,
            transaction.instigator,
            transaction.sourceObject,
            transaction.damageType,
            transaction.damage,
            transaction.stun,
            transaction.position,
            transaction.clock,
            transaction.canAttributeMortality,
            lethal));

        // A Die() reached from inside Creature.Violence is buffered until after Damage is
        // delivered. This preserves one causal order for every consumer: Damage -> Mortality.
        FlushPendingMortality(transaction.state);
    }

    /// <summary>
    /// Captures mortality facts immediately before base.Die. DB_Creature must run its rock and
    /// held-update early-return guards before calling this method.
    /// </summary>
    internal static MortalityTransaction PrepareMortality(DB_Creature victim)
    {
        if (!enabled || victim == null)
            return default;

        bool wasDead = victim.dead;
        VictimState state = StateFor(victim);
        int clock = Clock(victim);
        Vector2 deathPosition = victim.mainBodyChunk?.pos ?? Vector2.zero;
        DB_Creature[] chainWitnesses = !wasDead
            ? DB_FearRuntime.SnapshotChainWitnesses(victim)
            : Array.Empty<DB_Creature>();
        bool revengeFailed = !wasDead && DB_VengeanceRuntime.IsActive(victim);

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

        return new MortalityTransaction(
            state,
            victim,
            wasDead,
            clock,
            deathPosition,
            chainWitnesses,
            revengeFailed,
            killer,
            sourceObject,
            damageType,
            damage,
            stun,
            threatScale,
            wasConsumed,
            attribution);
    }

    /// <summary>
    /// Confirms the live -> dead transition after base.Die. Nested mortality remains buffered
    /// while a Violence transaction is active and is flushed only after its Damage event.
    /// </summary>
    internal static void CompleteMortality(in MortalityTransaction transaction)
    {
        if (!transaction.Active || transaction.wasDead || !transaction.victim.dead ||
            !transaction.state.TryMarkMortality())
            return;

        var mortality = new DB_MortalityEvent(
            NextSequence(),
            transaction.victim,
            transaction.killer,
            transaction.sourceObject,
            transaction.damageType,
            transaction.damage,
            transaction.stun,
            transaction.deathPosition,
            transaction.chainWitnesses,
            transaction.threatScale,
            transaction.revengeFailed,
            transaction.wasConsumed,
            transaction.clock,
            transaction.attribution);

        if (transaction.state.ViolenceDepth > 0)
            transaction.state.PendingMortality = mortality;
        else
            Dispatch(Mortality, mortality);
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
        DB_Creature victim = afterOwner as DB_Creature;
        bool enteredCapture =
            afterState == LizardTongue.State.AttachedInSmallObject &&
            victim != null && !victim.dead &&
            self.lizard != null &&
            (beforeState != LizardTongue.State.AttachedInSmallObject ||
             !ReferenceEquals(beforeOwner, afterOwner));

        if (enteredCapture)
            ReportCapture(victim, self.lizard, self, DB_CaptureKind.Tongue);
    }

    private static void ReportCapture(
        DB_Creature victim,
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

    private static bool CaptureStillActive(DB_Creature victim, CaptureSession session)
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

    private static int Clock(DB_Creature victim) =>
        victim?.room?.game?.clock ?? int.MinValue;

    private static VictimState StateFor(DB_Creature victim) =>
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
    internal readonly DB_Creature Victim;
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
        DB_Creature victim,
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
    internal readonly DB_Creature Victim;
    internal readonly Creature Captor;
    internal readonly LizardTongue Tongue;
    internal readonly DB_CaptureKind CaptureKind;
    internal readonly Vector2 Position;
    internal readonly int Clock;
    internal readonly int SessionSerial;

    internal DB_CaptureEvent(
        int sequence,
        DB_Creature victim,
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
    internal readonly DB_Creature Victim;
    internal readonly Creature Killer;
    internal readonly PhysicalObject SourceObject;
    internal readonly Creature.DamageType DamageType;
    internal readonly float Damage;
    internal readonly float Stun;
    internal readonly Vector2 Position;
    internal readonly DB_Creature[] ChainWitnesses;
    internal readonly float ThreatScale;
    internal readonly bool RevengeFailed;
    internal readonly bool WasConsumed;
    internal readonly int Clock;
    internal readonly DB_MortalityAttribution Attribution;

    internal DB_MortalityEvent(
        int sequence,
        DB_Creature victim,
        Creature killer,
        PhysicalObject sourceObject,
        Creature.DamageType damageType,
        float damage,
        float stun,
        Vector2 position,
        DB_Creature[] chainWitnesses,
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
        ChainWitnesses = chainWitnesses ?? Array.Empty<DB_Creature>();
        ThreatScale = Mathf.Max(0f, threatScale);
        RevengeFailed = revengeFailed;
        WasConsumed = wasConsumed;
        Clock = clock;
        Attribution = attribution;
    }
}