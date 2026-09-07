using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal delegate void DB_ThreatAdapter(
    PhysicalObject source,
    Creature.DamageType damageType,
    float damage,
    float stun,
    bool projectileContext,
    ref DB_ThreatEvidence evidence);

internal static class DB_ThreatClassifier
{
    private static readonly List<DB_ThreatAdapter> customAdapters = new();

    internal static void Register(DB_ThreatAdapter adapter)
    {
        if (adapter != null && !customAdapters.Contains(adapter)) customAdapters.Add(adapter);
    }

    internal static void Unregister(DB_ThreatAdapter adapter)
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
    internal static DB_ThreatEvidence Classify(
        PhysicalObject source,
        Creature.DamageType damageType,
        float damage,
        float stun,
        bool projectileContext)
    {
        DB_ThreatEvidence evidence = default;
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

    internal static DB_ThreatEvidence GrabEvidence() =>
        new DB_ThreatEvidence { GrabCapture = 0.58f };

    internal static DB_ThreatEvidence PursuitEvidence() =>
        new DB_ThreatEvidence { Pursuit = 0.30f };

    internal static DB_ThreatEvidence RetreatEvidence() =>
        new DB_ThreatEvidence { RetreatTendency = 0.085f };

    internal static DB_ThreatEvidence NonAggressionEvidence() =>
        new DB_ThreatEvidence { NonAggressionConfidence = 0.035f };

    internal static DB_ThreatEvidence CounterKillEvidence() =>
        new DB_ThreatEvidence { CounterKill = 0.42f };

    internal static DB_ThreatEvidence FirecrackerStartleEvidence() =>
        new DB_ThreatEvidence
        {
            Startle = 0.58f,
            AreaDenial = 0.08f
        };

    internal static DB_ThreatEvidence ExplosionEvidence(Explosion explosion)
    {
        if (explosion == null) return default;
        float damage = Mathf.Max(0f, explosion.damage);
        float stun = Mathf.Max(0f, explosion.stun);
        float radius = Mathf.Max(0f, explosion.rad);
        float strength = Mathf.Clamp01(0.14f + damage * 0.45f + stun / 500f + radius / 650f);
        var result = new DB_ThreatEvidence
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

        DB_ThreatEvidence sourceEvidence = Classify(
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
        ref DB_ThreatEvidence evidence)
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

    private static void Clamp(ref DB_ThreatEvidence evidence)
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
