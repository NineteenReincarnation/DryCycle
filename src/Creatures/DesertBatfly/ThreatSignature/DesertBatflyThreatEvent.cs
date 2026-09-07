using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

[Flags]
internal enum DesertBatflyThreatTag
{
    None = 0,
    Projectile = 1 << 0,
    Piercing = 1 << 1,
    BluntStun = 1 << 2,
    Explosion = 1 << 3,
    Startle = 1 << 4,
    Shock = 1 << 5,
    AreaDenial = 1 << 6,
    GrabCapture = 1 << 7,
    Pursuit = 1 << 8,
    CounterKill = 1 << 9,
    RetreatTendency = 1 << 10,
    NonAggression = 1 << 11
}

internal struct DesertBatflyThreatEvidence
{
    internal float Projectile;
    internal float Piercing;
    internal float BluntStun;
    internal float Explosion;
    internal float Startle;
    internal float Shock;
    internal float AreaDenial;
    internal float GrabCapture;
    internal float Pursuit;
    internal float CounterKill;
    internal float RetreatTendency;
    internal float NonAggressionConfidence;

    internal float Strongest => Mathf.Max(
        Mathf.Max(Mathf.Max(Projectile, Piercing), Mathf.Max(BluntStun, Explosion)),
        Mathf.Max(
            Mathf.Max(Mathf.Max(Startle, Shock), Mathf.Max(AreaDenial, GrabCapture)),
            Mathf.Max(Mathf.Max(Pursuit, CounterKill), Mathf.Max(RetreatTendency, NonAggressionConfidence))));

    internal bool Any => Strongest > 0f;

    internal DesertBatflyThreatTag Tags
    {
        get
        {
            DesertBatflyThreatTag tags = DesertBatflyThreatTag.None;
            if (Projectile > 0f) tags |= DesertBatflyThreatTag.Projectile;
            if (Piercing > 0f) tags |= DesertBatflyThreatTag.Piercing;
            if (BluntStun > 0f) tags |= DesertBatflyThreatTag.BluntStun;
            if (Explosion > 0f) tags |= DesertBatflyThreatTag.Explosion;
            if (Startle > 0f) tags |= DesertBatflyThreatTag.Startle;
            if (Shock > 0f) tags |= DesertBatflyThreatTag.Shock;
            if (AreaDenial > 0f) tags |= DesertBatflyThreatTag.AreaDenial;
            if (GrabCapture > 0f) tags |= DesertBatflyThreatTag.GrabCapture;
            if (Pursuit > 0f) tags |= DesertBatflyThreatTag.Pursuit;
            if (CounterKill > 0f) tags |= DesertBatflyThreatTag.CounterKill;
            if (RetreatTendency > 0f) tags |= DesertBatflyThreatTag.RetreatTendency;
            if (NonAggressionConfidence > 0f) tags |= DesertBatflyThreatTag.NonAggression;
            return tags;
        }
    }

    internal void Scale(float multiplier)
    {
        if (float.IsNaN(multiplier) || float.IsInfinity(multiplier)) multiplier = 0f;
        multiplier = Mathf.Max(0f, multiplier);
        Projectile *= multiplier;
        Piercing *= multiplier;
        BluntStun *= multiplier;
        Explosion *= multiplier;
        Startle *= multiplier;
        Shock *= multiplier;
        AreaDenial *= multiplier;
        GrabCapture *= multiplier;
        Pursuit *= multiplier;
        CounterKill *= multiplier;
        RetreatTendency *= multiplier;
        NonAggressionConfidence *= multiplier;
    }

    internal void Merge(in DesertBatflyThreatEvidence other)
    {
        Projectile = Mathf.Max(Projectile, other.Projectile);
        Piercing = Mathf.Max(Piercing, other.Piercing);
        BluntStun = Mathf.Max(BluntStun, other.BluntStun);
        Explosion = Mathf.Max(Explosion, other.Explosion);
        Startle = Mathf.Max(Startle, other.Startle);
        Shock = Mathf.Max(Shock, other.Shock);
        AreaDenial = Mathf.Max(AreaDenial, other.AreaDenial);
        GrabCapture = Mathf.Max(GrabCapture, other.GrabCapture);
        Pursuit = Mathf.Max(Pursuit, other.Pursuit);
        CounterKill = Mathf.Max(CounterKill, other.CounterKill);
        RetreatTendency = Mathf.Max(RetreatTendency, other.RetreatTendency);
        NonAggressionConfidence = Mathf.Max(NonAggressionConfidence, other.NonAggressionConfidence);
    }
}

internal readonly struct DesertBatflyThreatEvent
{
    internal readonly Player Instigator;
    internal readonly PhysicalObject SourceObject;
    internal readonly DesertBatfly Victim;
    internal readonly Vector2 Position;
    internal readonly DesertBatflyThreatEvidence Evidence;
    internal readonly bool DirectVictim;
    internal readonly bool Lethal;
    internal readonly float StunStrength;
    internal readonly string Reason;

    internal DesertBatflyThreatEvent(
        Player instigator,
        PhysicalObject sourceObject,
        DesertBatfly victim,
        Vector2 position,
        in DesertBatflyThreatEvidence evidence,
        bool directVictim,
        bool lethal,
        float stunStrength,
        string reason)
    {
        Instigator = instigator;
        SourceObject = sourceObject;
        Victim = victim;
        Position = position;
        Evidence = evidence;
        DirectVictim = directVictim;
        Lethal = lethal;
        StunStrength = Mathf.Max(0f, stunStrength);
        Reason = reason ?? string.Empty;
    }
}

internal delegate void DesertBatflyThreatAdapter(
    PhysicalObject source,
    Creature.DamageType damageType,
    float damage,
    float stun,
    bool projectileContext,
    ref DesertBatflyThreatEvidence evidence);

internal static class DesertBatflyThreatAdapterRegistry
{
    private static readonly List<DesertBatflyThreatAdapter> customAdapters = new();

    internal static void Register(DesertBatflyThreatAdapter adapter)
    {
        if (adapter != null && !customAdapters.Contains(adapter)) customAdapters.Add(adapter);
    }

    internal static void Unregister(DesertBatflyThreatAdapter adapter)
    {
        if (adapter != null) customAdapters.Remove(adapter);
    }

    internal static void ResetCustomAdapters()
    {
        customAdapters.Clear();
    }

    /// <summary>
    /// Classifies one observation/event. The all-zero, non-projectile form is an
    /// observation-only Current Cue query: item identity may expose potential Explosion,
    /// Startle or Shock. Any real projectile/hit event uses evidence semantics instead,
    /// where those dimensions require the corresponding event to have actually happened.
    /// </summary>
    internal static DesertBatflyThreatEvidence Classify(
        PhysicalObject source,
        Creature.DamageType damageType,
        float damage,
        float stun,
        bool projectileContext)
    {
        DesertBatflyThreatEvidence evidence = default;
        bool cueOnly = damageType == null && damage <= 0f && stun <= 0f && !projectileContext;
        ApplyBuiltIns(source, damageType, damage, stun, projectileContext, cueOnly, ref evidence);
        for (int i = 0; i < customAdapters.Count; i++)
        {
            try { customAdapters[i](source, damageType, damage, stun, projectileContext, ref evidence); }
            catch { }
        }
        Clamp(ref evidence);
        return evidence;
    }

    internal static DesertBatflyThreatEvidence GrabEvidence() =>
        new DesertBatflyThreatEvidence { GrabCapture = 0.58f };

    internal static DesertBatflyThreatEvidence PursuitEvidence() =>
        new DesertBatflyThreatEvidence { Pursuit = 0.30f };

    internal static DesertBatflyThreatEvidence RetreatEvidence() =>
        new DesertBatflyThreatEvidence { RetreatTendency = 0.085f };

    internal static DesertBatflyThreatEvidence NonAggressionEvidence() =>
        new DesertBatflyThreatEvidence { NonAggressionConfidence = 0.035f };

    internal static DesertBatflyThreatEvidence CounterKillEvidence() =>
        new DesertBatflyThreatEvidence { CounterKill = 0.42f };

    internal static DesertBatflyThreatEvidence FirecrackerStartleEvidence() =>
        new DesertBatflyThreatEvidence
        {
            Startle = 0.58f,
            AreaDenial = 0.08f
        };

    internal static DesertBatflyThreatEvidence ExplosionEvidence(Explosion explosion)
    {
        if (explosion == null) return default;
        float damage = Mathf.Max(0f, explosion.damage);
        float stun = Mathf.Max(0f, explosion.stun);
        float radius = Mathf.Max(0f, explosion.rad);
        float strength = Mathf.Clamp01(0.14f + damage * 0.45f + stun / 500f + radius / 650f);
        var result = new DesertBatflyThreatEvidence
        {
            Explosion = Mathf.Clamp01(strength * 0.72f),
            AreaDenial = Mathf.Clamp01(strength * 0.42f)
        };

        PhysicalObject source = explosion.sourceObject;
        bool firecracker = source is FirecrackerPlant;
        if (firecracker)
        {
            result.Explosion = Mathf.Min(result.Explosion, 0.10f);
            result.AreaDenial = Mathf.Min(result.AreaDenial, 0.08f);
            result.Startle = Mathf.Max(result.Startle, 0.20f);
        }

        DesertBatflyThreatEvidence sourceEvidence = Classify(
            source,
            Creature.DamageType.Explosion,
            damage,
            stun,
            source is Weapon);
        result.Merge(sourceEvidence);
        if (firecracker)
        {
            result.Explosion = Mathf.Min(result.Explosion, 0.10f);
            result.AreaDenial = Mathf.Min(result.AreaDenial, 0.08f);
        }
        Clamp(ref result);
        return result;
    }

    private static void ApplyBuiltIns(
        PhysicalObject source,
        Creature.DamageType damageType,
        float damage,
        float stun,
        bool projectileContext,
        bool cueOnly,
        ref DesertBatflyThreatEvidence evidence)
    {
        if (source is Weapon && projectileContext)
            evidence.Projectile = Mathf.Max(evidence.Projectile, 0.28f);

        if (source is Rock)
        {
            evidence.Projectile = Mathf.Max(evidence.Projectile, projectileContext ? 0.34f : 0.16f);
            evidence.BluntStun = Mathf.Max(evidence.BluntStun, Mathf.Clamp01(0.42f + stun / 260f));
        }

        if (source is Spear)
        {
            evidence.Projectile = Mathf.Max(evidence.Projectile, projectileContext ? 0.34f : 0.18f);
            evidence.Piercing = Mathf.Max(evidence.Piercing, Mathf.Clamp01(0.48f + Mathf.Max(0f, damage) * 0.12f));
        }

        string typeName = source != null ? source.GetType().Name : string.Empty;

        // Item identity belongs to Current Cue only. Merely recognizing an explosive,
        // firecracker or electric item must never train its long-term danger mode.
        if (cueOnly)
        {
            if (source is FirecrackerPlant)
            {
                evidence.Startle = Mathf.Max(evidence.Startle, 0.54f);
                evidence.AreaDenial = Mathf.Max(evidence.AreaDenial, 0.06f);
            }
            if (Contains(typeName, "Explosive") || Contains(typeName, "Bomb") || Contains(typeName, "Grenade"))
            {
                evidence.Explosion = Mathf.Max(evidence.Explosion, 0.48f);
                evidence.AreaDenial = Mathf.Max(evidence.AreaDenial, 0.24f);
            }
            if (Contains(typeName, "Electric") || Contains(typeName, "Shock"))
                evidence.Shock = Mathf.Max(evidence.Shock, 0.46f);
        }

        // Persistent Shock/Explosion evidence comes from real event semantics, not names.
        if (damageType == Creature.DamageType.Blunt)
            evidence.BluntStun = Mathf.Max(evidence.BluntStun, Mathf.Clamp01(0.24f + stun / 300f));
        if (damageType == Creature.DamageType.Electric)
            evidence.Shock = Mathf.Max(evidence.Shock, Mathf.Clamp01(0.48f + stun / 350f));
        if (damageType == Creature.DamageType.Explosion)
        {
            evidence.Explosion = Mathf.Max(evidence.Explosion, Mathf.Clamp01(0.28f + Mathf.Max(0f, damage) * 0.35f));
            evidence.AreaDenial = Mathf.Max(evidence.AreaDenial, 0.18f);
        }
    }

    private static bool Contains(string text, string token) =>
        !string.IsNullOrEmpty(text) && text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void Clamp(ref DesertBatflyThreatEvidence evidence)
    {
        evidence.Projectile = Safe01(evidence.Projectile);
        evidence.Piercing = Safe01(evidence.Piercing);
        evidence.BluntStun = Safe01(evidence.BluntStun);
        evidence.Explosion = Safe01(evidence.Explosion);
        evidence.Startle = Safe01(evidence.Startle);
        evidence.Shock = Safe01(evidence.Shock);
        evidence.AreaDenial = Safe01(evidence.AreaDenial);
        evidence.GrabCapture = Safe01(evidence.GrabCapture);
        evidence.Pursuit = Safe01(evidence.Pursuit);
        evidence.CounterKill = Safe01(evidence.CounterKill);
        evidence.RetreatTendency = Safe01(evidence.RetreatTendency);
        evidence.NonAggressionConfidence = Safe01(evidence.NonAggressionConfidence);
    }

    private static float Safe01(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
}
