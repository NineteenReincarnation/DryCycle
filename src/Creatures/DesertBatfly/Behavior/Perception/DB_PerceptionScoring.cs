using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Pure, order-independent scoring for Perception R2. These functions interpret already
/// observed facts only; they never scan rooms, write AI state or own locomotion.
/// </summary>
internal static class DB_PerceptionScoring
{
    internal const float AttentionSwitchRatio = 1.14f;
    internal const float AttentionSwitchMargin = 0.045f;
    internal const int LostTrackMaxTicks = 52;
    internal const float LostTrackMaxPredictionDistance = 145f;

    internal static float DirectConfidence(
        float baseRange,
        float distance,
        float visibilityConfidence)
    {
        if (baseRange <= 0f || distance < 0f) return 0f;
        visibilityConfidence = Mathf.Clamp01(visibilityConfidence);
        float rangeFactor = 1f - Mathf.Clamp01(distance / Mathf.Max(1f, baseRange));
        float weatherFactor = Mathf.Lerp(0.34f, 1f, visibilityConfidence);
        return Mathf.Clamp01((0.58f + rangeFactor * 0.42f) * weatherFactor);
    }

    internal static float ThreatAttentionScore(
        float relationshipDanger,
        float distance,
        float threatDistance,
        float closingSpeed,
        float actionDanger,
        float confidence,
        float memoryBias,
        float nerve)
    {
        relationshipDanger = Mathf.Clamp01(relationshipDanger);
        confidence = Mathf.Clamp01(confidence);
        actionDanger = Mathf.Clamp01(actionDanger);
        memoryBias = Mathf.Clamp01(memoryBias);
        nerve = Mathf.Clamp01(nerve);
        if (relationshipDanger <= 0f || confidence <= 0f) return 0f;

        float proximity = threatDistance <= 0f
            ? 0f
            : 1f - Mathf.Clamp01(distance / threatDistance);
        float closing = Mathf.InverseLerp(-2f, 8f, closingSpeed);
        float ambiguitySensitivity = Mathf.Lerp(1.16f, 0.82f, nerve);
        float urgency = Mathf.Clamp01(
            relationshipDanger * 0.28f +
            proximity * 0.31f +
            closing * 0.18f +
            actionDanger * 0.13f +
            memoryBias * 0.10f);
        return Mathf.Clamp01(urgency * confidence * ambiguitySensitivity);
    }

    internal static float ProjectileRisk(
        float timeToClosestApproach,
        float closestDistance,
        float missRadius,
        float speed,
        float lethality,
        float confidence,
        float instigatorMemory)
    {
        if (timeToClosestApproach < 0f || missRadius <= 0f || speed < 0f) return 0f;
        lethality = Mathf.Clamp01(lethality);
        confidence = Mathf.Clamp01(confidence);
        instigatorMemory = Mathf.Clamp01(instigatorMemory);
        if (confidence <= 0f) return 0f;

        float timeUrgency = 1f - Mathf.Clamp01(timeToClosestApproach / 2.25f);
        float missUrgency = 1f - Mathf.Clamp01(closestDistance / missRadius);
        float speedUrgency = Mathf.InverseLerp(1.25f, 18f, speed);
        float risk =
            timeUrgency * 0.34f +
            missUrgency * 0.32f +
            speedUrgency * 0.13f +
            lethality * 0.17f +
            instigatorMemory * 0.04f;
        return Mathf.Clamp01(risk * confidence);
    }

    internal static float SignalConfidence(
        float packetIntensity,
        float transportAttenuation,
        int hop,
        float conformity,
        float nerve,
        bool acoustic)
    {
        packetIntensity = Mathf.Clamp01(packetIntensity);
        transportAttenuation = Mathf.Clamp01(transportAttenuation);
        conformity = Mathf.Clamp01(conformity);
        nerve = Mathf.Clamp01(nerve);
        hop = Mathf.Max(0, hop);

        float trust = Mathf.Lerp(0.72f, 1.18f, conformity);
        float alarmSensitivity = Mathf.Lerp(1.10f, 0.88f, nerve);
        float modality = acoustic ? 0.82f : 1f;
        // Transport already attenuates explicit relays. This second monotonic factor prevents
        // personality from making a remote report more certain than the same root evidence.
        float relay = Mathf.Pow(0.82f, hop);
        return Mathf.Clamp01(
            packetIntensity * transportAttenuation * trust * alarmSensitivity * modality * relay);
    }

    internal static float LostConfidence(float initialConfidence, int ageTicks)
    {
        initialConfidence = Mathf.Clamp01(initialConfidence);
        if (ageTicks <= 0) return initialConfidence;
        if (ageTicks >= LostTrackMaxTicks) return 0f;
        float life = 1f - ageTicks / (float)LostTrackMaxTicks;
        return Mathf.Clamp01(initialConfidence * life * life);
    }

    internal static Vector2 PredictLostPosition(
        Vector2 lastPosition,
        Vector2 lastVelocity,
        int ageTicks)
    {
        if (ageTicks <= 0 || lastVelocity.sqrMagnitude <= 0.0001f) return lastPosition;
        Vector2 delta = lastVelocity * Mathf.Min(ageTicks, LostTrackMaxTicks);
        if (delta.sqrMagnitude > LostTrackMaxPredictionDistance * LostTrackMaxPredictionDistance)
            delta = delta.normalized * LostTrackMaxPredictionDistance;
        return lastPosition + delta;
    }

    internal static bool ShouldSwitchAttention(
        float currentScore,
        float challengerScore,
        float currentConfidence,
        bool challengerImminent)
    {
        currentScore = Mathf.Max(0f, currentScore);
        challengerScore = Mathf.Max(0f, challengerScore);
        currentConfidence = Mathf.Clamp01(currentConfidence);
        if (challengerImminent) return challengerScore > 0f;
        if (currentScore <= 0f || currentConfidence <= 0.08f) return challengerScore > currentScore;
        float required = currentScore * AttentionSwitchRatio + AttentionSwitchMargin;
        return challengerScore > required;
    }

    internal static bool BetterScore(
        float challengerScore,
        int challengerStableKey,
        float currentScore,
        int currentStableKey)
    {
        if (challengerScore > currentScore + 0.0001f) return true;
        if (challengerScore + 0.0001f < currentScore) return false;
        return challengerStableKey < currentStableKey;
    }
}
