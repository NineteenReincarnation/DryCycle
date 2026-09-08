using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

[Flags]
internal enum DB_ThreatTag
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

internal struct DB_ThreatEvidence
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

    internal DB_ThreatTag Tags
    {
        get
        {
            DB_ThreatTag tags = DB_ThreatTag.None;
            if (Projectile > 0f) tags |= DB_ThreatTag.Projectile;
            if (Piercing > 0f) tags |= DB_ThreatTag.Piercing;
            if (BluntStun > 0f) tags |= DB_ThreatTag.BluntStun;
            if (Explosion > 0f) tags |= DB_ThreatTag.Explosion;
            if (Startle > 0f) tags |= DB_ThreatTag.Startle;
            if (Shock > 0f) tags |= DB_ThreatTag.Shock;
            if (AreaDenial > 0f) tags |= DB_ThreatTag.AreaDenial;
            if (GrabCapture > 0f) tags |= DB_ThreatTag.GrabCapture;
            if (Pursuit > 0f) tags |= DB_ThreatTag.Pursuit;
            if (CounterKill > 0f) tags |= DB_ThreatTag.CounterKill;
            if (RetreatTendency > 0f) tags |= DB_ThreatTag.RetreatTendency;
            if (NonAggressionConfidence > 0f) tags |= DB_ThreatTag.NonAggression;
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

    internal void Merge(in DB_ThreatEvidence other)
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

