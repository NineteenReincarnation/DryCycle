using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DesertBatflyThreatDimension
{
    Projectile,
    Piercing,
    BluntStun,
    Explosion,
    Startle,
    Shock,
    AreaDenial,
    GrabCapture,
    Pursuit,
    CounterKill,
    RetreatTendency,
    NonAggressionConfidence
}

internal sealed class DesertBatflyPlayerThreatMemory
{
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
    internal float Confidence;
    internal int LastMeaningfulEncounterCycle = -1;
    internal int LastDecayCycle = -1;

    internal float Get(DesertBatflyThreatDimension dimension)
    {
        return dimension switch
        {
            DesertBatflyThreatDimension.Projectile => ProjectilePressure,
            DesertBatflyThreatDimension.Piercing => PiercingPressure,
            DesertBatflyThreatDimension.BluntStun => BluntStunPressure,
            DesertBatflyThreatDimension.Explosion => ExplosionPressure,
            DesertBatflyThreatDimension.Startle => StartlePressure,
            DesertBatflyThreatDimension.Shock => ShockPressure,
            DesertBatflyThreatDimension.AreaDenial => AreaDenialPressure,
            DesertBatflyThreatDimension.GrabCapture => GrabCapturePressure,
            DesertBatflyThreatDimension.Pursuit => PursuitPressure,
            DesertBatflyThreatDimension.CounterKill => CounterKillPressure,
            DesertBatflyThreatDimension.RetreatTendency => RetreatTendency,
            DesertBatflyThreatDimension.NonAggressionConfidence => NonAggressionConfidence,
            _ => 0f
        };
    }

    internal void Add(DesertBatflyThreatDimension dimension, float evidence)
    {
        if (!FinitePositive(evidence)) return;
        evidence = Mathf.Clamp01(evidence);
        switch (dimension)
        {
            case DesertBatflyThreatDimension.Projectile:
                ProjectilePressure = Saturating(ProjectilePressure, evidence);
                break;
            case DesertBatflyThreatDimension.Piercing:
                PiercingPressure = Saturating(PiercingPressure, evidence);
                break;
            case DesertBatflyThreatDimension.BluntStun:
                BluntStunPressure = Saturating(BluntStunPressure, evidence);
                break;
            case DesertBatflyThreatDimension.Explosion:
                ExplosionPressure = Saturating(ExplosionPressure, evidence);
                break;
            case DesertBatflyThreatDimension.Startle:
                StartlePressure = Saturating(StartlePressure, evidence);
                break;
            case DesertBatflyThreatDimension.Shock:
                ShockPressure = Saturating(ShockPressure, evidence);
                break;
            case DesertBatflyThreatDimension.AreaDenial:
                AreaDenialPressure = Saturating(AreaDenialPressure, evidence);
                break;
            case DesertBatflyThreatDimension.GrabCapture:
                GrabCapturePressure = Saturating(GrabCapturePressure, evidence);
                break;
            case DesertBatflyThreatDimension.Pursuit:
                PursuitPressure = Saturating(PursuitPressure, evidence);
                break;
            case DesertBatflyThreatDimension.CounterKill:
                CounterKillPressure = Saturating(CounterKillPressure, evidence);
                break;
            case DesertBatflyThreatDimension.RetreatTendency:
                RetreatTendency = Saturating(RetreatTendency, evidence);
                break;
            case DesertBatflyThreatDimension.NonAggressionConfidence:
                NonAggressionConfidence = Saturating(NonAggressionConfidence, evidence);
                break;
        }
    }

    internal void ApplyDecay(int cycles)
    {
        if (cycles <= 0) return;
        float signatureFactor = Mathf.Pow(DesertBatflyThreatMemoryStore.SignatureDecayPerCycle, cycles);
        float confidenceFactor = Mathf.Pow(DesertBatflyThreatMemoryStore.ConfidenceDecayPerCycle, cycles);
        ProjectilePressure *= signatureFactor;
        PiercingPressure *= signatureFactor;
        BluntStunPressure *= signatureFactor;
        ExplosionPressure *= signatureFactor;
        StartlePressure *= signatureFactor;
        ShockPressure *= signatureFactor;
        AreaDenialPressure *= signatureFactor;
        GrabCapturePressure *= signatureFactor;
        PursuitPressure *= signatureFactor;
        CounterKillPressure *= signatureFactor;
        RetreatTendency *= signatureFactor;
        NonAggressionConfidence *= signatureFactor;
        Confidence *= confidenceFactor;
        Sanitize();
    }

    internal void Sanitize()
    {
        ProjectilePressure = Safe01(ProjectilePressure);
        PiercingPressure = Safe01(PiercingPressure);
        BluntStunPressure = Safe01(BluntStunPressure);
        ExplosionPressure = Safe01(ExplosionPressure);
        StartlePressure = Safe01(StartlePressure);
        ShockPressure = Safe01(ShockPressure);
        AreaDenialPressure = Safe01(AreaDenialPressure);
        GrabCapturePressure = Safe01(GrabCapturePressure);
        PursuitPressure = Safe01(PursuitPressure);
        CounterKillPressure = Safe01(CounterKillPressure);
        RetreatTendency = Safe01(RetreatTendency);
        NonAggressionConfidence = Safe01(NonAggressionConfidence);
        Confidence = Safe01(Confidence);
    }

    internal static float Saturating(float current, float evidence)
    {
        current = Safe01(current);
        if (!FinitePositive(evidence)) return current;
        return Mathf.Clamp01(current + Mathf.Clamp01(evidence) * (1f - current));
    }

    private static float Safe01(float value) =>
        float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);

    private static bool FinitePositive(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
}

internal sealed class DesertBatflyThreatMemorySet
{
    internal const int PlayerSlots = 4;
    internal readonly DesertBatflyPlayerThreatMemory[] Players =
    {
        new(), new(), new(), new()
    };
}

internal static class DesertBatflyThreatMemoryStore
{
    internal const string SaveKey = "DCDesertBatflyThreatV1";
    internal const float SignatureDecayPerCycle = 0.80f;
    internal const float ConfidenceDecayPerCycle = 0.86f;

    private static ConditionalWeakTable<DesertBatflyState, DesertBatflyThreatMemorySet> memories = new();

    internal static void ResetRuntime()
    {
        memories = new ConditionalWeakTable<DesertBatflyState, DesertBatflyThreatMemorySet>();
    }

    internal static DesertBatflyPlayerThreatMemory For(DesertBatflyState state, int playerSlot)
    {
        if (state == null || playerSlot < 0 || playerSlot >= DesertBatflyThreatMemorySet.PlayerSlots)
            return null;
        return SetFor(state).Players[playerSlot];
    }

    internal static DesertBatflyThreatMemorySet SetFor(DesertBatflyState state)
    {
        if (state == null) return null;
        if (memories.TryGetValue(state, out DesertBatflyThreatMemorySet existing))
            return existing;

        var created = new DesertBatflyThreatMemorySet();
        Load(state, created);
        memories.Add(state, created);
        return created;
    }

    internal static void AddEvidence(
        DesertBatflyState state,
        int playerSlot,
        in DesertBatflyThreatEvidence evidence,
        float multiplier,
        int cycle)
    {
        DesertBatflyPlayerThreatMemory memory = For(state, playerSlot);
        if (memory == null || float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f)
            return;

        multiplier = Mathf.Clamp(multiplier, 0f, 1.5f);
        memory.Add(DesertBatflyThreatDimension.Projectile, evidence.Projectile * multiplier);
        memory.Add(DesertBatflyThreatDimension.Piercing, evidence.Piercing * multiplier);
        memory.Add(DesertBatflyThreatDimension.BluntStun, evidence.BluntStun * multiplier);
        memory.Add(DesertBatflyThreatDimension.Explosion, evidence.Explosion * multiplier);
        memory.Add(DesertBatflyThreatDimension.Startle, evidence.Startle * multiplier);
        memory.Add(DesertBatflyThreatDimension.Shock, evidence.Shock * multiplier);
        memory.Add(DesertBatflyThreatDimension.AreaDenial, evidence.AreaDenial * multiplier);
        memory.Add(DesertBatflyThreatDimension.GrabCapture, evidence.GrabCapture * multiplier);
        memory.Add(DesertBatflyThreatDimension.Pursuit, evidence.Pursuit * multiplier);
        memory.Add(DesertBatflyThreatDimension.CounterKill, evidence.CounterKill * multiplier);
        memory.Add(DesertBatflyThreatDimension.RetreatTendency, evidence.RetreatTendency * multiplier);
        memory.Add(DesertBatflyThreatDimension.NonAggressionConfidence, evidence.NonAggressionConfidence * multiplier);

        float strongest = evidence.Strongest * multiplier;
        if (strongest > 0f)
            memory.Confidence = DesertBatflyPlayerThreatMemory.Saturating(
                memory.Confidence,
                Mathf.Clamp01(strongest * 0.55f));
        if (cycle >= 0)
        {
            memory.LastMeaningfulEncounterCycle = cycle;
            if (memory.LastDecayCycle < 0) memory.LastDecayCycle = cycle;
        }
        memory.Sanitize();
        Sync(state);
    }

    internal static void DecayToCycle(DesertBatflyState state, int currentCycle)
    {
        if (state == null || currentCycle < 0) return;
        DesertBatflyThreatMemorySet set = SetFor(state);
        bool changed = false;
        for (int i = 0; i < set.Players.Length; i++)
        {
            DesertBatflyPlayerThreatMemory memory = set.Players[i];
            if (memory.LastDecayCycle < 0)
            {
                memory.LastDecayCycle = currentCycle;
                changed = true;
                continue;
            }
            if (currentCycle <= memory.LastDecayCycle) continue;
            int elapsed = Mathf.Clamp(currentCycle - memory.LastDecayCycle, 1, 1000);
            memory.ApplyDecay(elapsed);
            memory.LastDecayCycle = currentCycle;
            changed = true;
        }
        if (changed) Sync(state);
    }

    internal static string DominantSignature(DesertBatflyPlayerThreatMemory memory)
    {
        if (memory == null || memory.Confidence < 0.04f) return "None";
        float best = 0f;
        DesertBatflyThreatDimension bestDimension = DesertBatflyThreatDimension.Projectile;
        foreach (DesertBatflyThreatDimension dimension in Enum.GetValues(typeof(DesertBatflyThreatDimension)))
        {
            float value = memory.Get(dimension);
            if (value <= best) continue;
            best = value;
            bestDimension = dimension;
        }
        return best < 0.08f ? "None" : bestDimension.ToString();
    }

    internal static void Sync(DesertBatflyState state)
    {
        if (state == null || !memories.TryGetValue(state, out DesertBatflyThreatMemorySet set)) return;
        state.unrecognizedSaveStrings[SaveKey] = Serialize(set);
    }

    private static string Serialize(DesertBatflyThreatMemorySet set)
    {
        string[] slots = new string[DesertBatflyThreatMemorySet.PlayerSlots + 1];
        slots[0] = "1";
        for (int i = 0; i < DesertBatflyThreatMemorySet.PlayerSlots; i++)
        {
            DesertBatflyPlayerThreatMemory m = set.Players[i];
            m.Sanitize();
            slots[i + 1] = string.Join("|", new[]
            {
                F(m.ProjectilePressure), F(m.PiercingPressure), F(m.BluntStunPressure),
                F(m.ExplosionPressure), F(m.StartlePressure), F(m.ShockPressure),
                F(m.AreaDenialPressure), F(m.GrabCapturePressure), F(m.PursuitPressure),
                F(m.CounterKillPressure), F(m.RetreatTendency), F(m.NonAggressionConfidence),
                F(m.Confidence), m.LastMeaningfulEncounterCycle.ToString(CultureInfo.InvariantCulture),
                m.LastDecayCycle.ToString(CultureInfo.InvariantCulture)
            });
        }
        return string.Join("/", slots);
    }

    private static void Load(DesertBatflyState state, DesertBatflyThreatMemorySet set)
    {
        if (!state.unrecognizedSaveStrings.TryGetValue(SaveKey, out string raw) || string.IsNullOrEmpty(raw))
            return;
        string[] slots = raw.Split('/');
        if (slots.Length < 2 || slots[0] != "1") return;
        int count = Mathf.Min(DesertBatflyThreatMemorySet.PlayerSlots, slots.Length - 1);
        for (int i = 0; i < count; i++)
        {
            string[] values = slots[i + 1].Split('|');
            if (values.Length < 13) continue;
            DesertBatflyPlayerThreatMemory m = set.Players[i];
            m.ProjectilePressure = P(values, 0);
            m.PiercingPressure = P(values, 1);
            m.BluntStunPressure = P(values, 2);
            m.ExplosionPressure = P(values, 3);
            m.StartlePressure = P(values, 4);
            m.ShockPressure = P(values, 5);
            m.AreaDenialPressure = P(values, 6);
            m.GrabCapturePressure = P(values, 7);
            m.PursuitPressure = P(values, 8);
            m.CounterKillPressure = P(values, 9);
            m.RetreatTendency = P(values, 10);
            m.NonAggressionConfidence = P(values, 11);
            m.Confidence = P(values, 12);
            if (values.Length > 13 && int.TryParse(values[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out int encounter))
                m.LastMeaningfulEncounterCycle = encounter;
            if (values.Length > 14 && int.TryParse(values[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out int decay))
                m.LastDecayCycle = decay;
            m.Sanitize();
        }
    }

    private static string F(float value) =>
        (float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value))
        .ToString("R", CultureInfo.InvariantCulture);

    private static float P(string[] values, int index)
    {
        if (index < 0 || index >= values.Length ||
            !float.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            float.IsNaN(value) || float.IsInfinity(value))
            return 0f;
        return Mathf.Clamp01(value);
    }
}
