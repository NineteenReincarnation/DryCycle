using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_ThreatEvent
{
    internal readonly Player Instigator;
    internal readonly PhysicalObject SourceObject;
    internal readonly DB_Creature Victim;
    internal readonly Vector2 Position;
    internal readonly DB_ThreatEvidence Evidence;
    internal readonly bool DirectVictim;
    internal readonly bool Lethal;
    internal readonly float StunStrength;
    internal readonly string Reason;

    internal DB_ThreatEvent(
        Player instigator,
        PhysicalObject sourceObject,
        DB_Creature victim,
        Vector2 position,
        in DB_ThreatEvidence evidence,
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

