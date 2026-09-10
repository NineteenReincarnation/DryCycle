#!/usr/bin/env python3
"""Deterministic behavioral invariants for DesertBatfly Perception R2.

This is not a Unity simulation. It mirrors the pure scoring contracts in
DB_PerceptionScoring and stress-tests ordering, monotonicity and boundedness.
"""
from __future__ import annotations

import itertools
import math
import random

ATTENTION_SWITCH_RATIO = 1.14
ATTENTION_SWITCH_MARGIN = 0.045
LOST_TRACK_MAX_TICKS = 52


def clamp01(v: float) -> float:
    return max(0.0, min(1.0, v))


def inverse_lerp(a: float, b: float, v: float) -> float:
    if a == b:
        return 0.0
    return clamp01((v - a) / (b - a))


def direct_confidence(base_range: float, distance: float, visibility: float) -> float:
    if base_range <= 0.0 or distance < 0.0:
        return 0.0
    visibility = clamp01(visibility)
    range_factor = 1.0 - clamp01(distance / max(1.0, base_range))
    weather_factor = 0.34 + (1.0 - 0.34) * visibility
    return clamp01((0.58 + range_factor * 0.42) * weather_factor)


def threat_score(rel: float, distance: float, threat_distance: float, closing: float,
                 action: float, confidence: float, memory: float, nerve: float) -> float:
    rel = clamp01(rel)
    confidence = clamp01(confidence)
    action = clamp01(action)
    memory = clamp01(memory)
    nerve = clamp01(nerve)
    if rel <= 0.0 or confidence <= 0.0:
        return 0.0
    proximity = 0.0 if threat_distance <= 0.0 else 1.0 - clamp01(distance / threat_distance)
    closing_term = inverse_lerp(-2.0, 8.0, closing)
    ambiguity = 1.16 + (0.82 - 1.16) * nerve
    urgency = clamp01(rel * 0.28 + proximity * 0.31 + closing_term * 0.18 + action * 0.13 + memory * 0.10)
    return clamp01(urgency * confidence * ambiguity)


def projectile_risk(tca: float, closest: float, miss_radius: float, speed: float,
                    lethality: float, confidence: float, memory: float) -> float:
    if tca < 0.0 or miss_radius <= 0.0 or speed < 0.0:
        return 0.0
    confidence = clamp01(confidence)
    if confidence <= 0.0:
        return 0.0
    risk = (
        (1.0 - clamp01(tca / 2.25)) * 0.34
        + (1.0 - clamp01(closest / miss_radius)) * 0.32
        + inverse_lerp(1.25, 18.0, speed) * 0.13
        + clamp01(lethality) * 0.17
        + clamp01(memory) * 0.04
    )
    return clamp01(risk * confidence)


def signal_confidence(intensity: float, attenuation: float, hop: int,
                      conformity: float, nerve: float, acoustic: bool) -> float:
    trust = 0.72 + (1.18 - 0.72) * clamp01(conformity)
    sensitivity = 1.10 + (0.88 - 1.10) * clamp01(nerve)
    modality = 0.82 if acoustic else 1.0
    relay = 0.82 ** max(0, hop)
    return clamp01(clamp01(intensity) * clamp01(attenuation) * trust * sensitivity * modality * relay)


def lost_confidence(initial: float, age: int) -> float:
    initial = clamp01(initial)
    if age <= 0:
        return initial
    if age >= LOST_TRACK_MAX_TICKS:
        return 0.0
    life = 1.0 - age / LOST_TRACK_MAX_TICKS
    return clamp01(initial * life * life)


def should_switch(current: float, challenger: float, confidence: float, imminent: bool) -> bool:
    current = max(0.0, current)
    challenger = max(0.0, challenger)
    confidence = clamp01(confidence)
    if imminent:
        return challenger > 0.0
    if current <= 0.0 or confidence <= 0.08:
        return challenger > current
    return challenger > current * ATTENTION_SWITCH_RATIO + ATTENTION_SWITCH_MARGIN


def choose_ranked(items):
    """Mirror the R2 score/key tie-break used by creature attention."""
    best = None
    for item in items:
        score, stable_key, name = item
        if best is None or score > best[0] + 1e-4 or (
            abs(score - best[0]) <= 1e-4 and stable_key < best[1]
        ):
            best = item
    return best


def choose_projectile(items):
    """Mirror risk -> TCA -> closest -> stable-key ordering."""
    best = None
    for item in items:
        risk, tca, closest, key, name = item
        if best is None:
            best = item
            continue
        better = (
            risk > best[0] + 1e-4
            or (abs(risk - best[0]) <= 1e-4 and (
                tca < best[1] - 1e-4
                or (abs(tca - best[1]) <= 1e-4 and (
                    closest < best[2] - 1e-4
                    or (abs(closest - best[2]) <= 1e-4 and key < best[3])
                ))
            ))
        )
        if better:
            best = item
    return best


def main() -> None:
    rng = random.Random(0xD35E47)

    # 1. Direct threat selection must be independent of candidate enumeration order.
    for _ in range(5000):
        count = rng.randint(2, 6)
        candidates = []
        for i in range(count):
            score = threat_score(
                rng.uniform(0.4, 1.0), rng.uniform(20, 250), rng.uniform(80, 280),
                rng.uniform(-4, 10), rng.random(), rng.uniform(0.3, 1.0),
                rng.random(), rng.random())
            candidates.append((score, rng.randrange(1, 1_000_000), f"c{i}"))
        expected = choose_ranked(candidates)
        for _ in range(4):
            shuffled = list(candidates)
            rng.shuffle(shuffled)
            assert choose_ranked(shuffled) == expected

    # 2. Projectile selection must also be order-independent and favor actual intersection risk.
    for _ in range(5000):
        projectiles = []
        for i in range(rng.randint(2, 6)):
            tca = rng.uniform(0.0, 3.0)
            closest = rng.uniform(0.0, 38.0)
            speed = rng.uniform(2.0, 20.0)
            risk = projectile_risk(tca, closest, 38.0, speed, rng.random(), rng.uniform(0.35, 1.0), rng.random())
            projectiles.append((risk, tca, closest, rng.randrange(1, 1_000_000), f"p{i}"))
        expected = choose_projectile(projectiles)
        for _ in range(4):
            shuffled = list(projectiles)
            rng.shuffle(shuffled)
            assert choose_projectile(shuffled) == expected

    grazing_rock = projectile_risk(1.8, 28.0, 38.0, 7.0, 0.34, 0.92, 0.0)
    imminent_spear = projectile_risk(0.30, 3.0, 38.0, 15.0, 1.0, 0.92, 0.0)
    assert imminent_spear > grazing_rock

    # 3. Relay reports may never become more certain as hop count increases.
    for _ in range(10000):
        args = (rng.random(), rng.random(), rng.random(), rng.random())
        root = signal_confidence(args[0], args[1], 0, args[2], args[3], False)
        hop1 = signal_confidence(args[0], args[1], 1, args[2], args[3], False)
        hop2 = signal_confidence(args[0], args[1], 2, args[2], args[3], False)
        assert root + 1e-9 >= hop1 >= hop2 - 1e-9
        acoustic = signal_confidence(args[0], args[1], 0, args[2], args[3], True)
        assert acoustic <= root + 1e-9

    # 4. Lost target belief is bounded and strictly non-increasing.
    for initial in (0.15, 0.35, 0.7, 1.0):
        values = [lost_confidence(initial, age) for age in range(LOST_TRACK_MAX_TICKS + 1)]
        assert all(a + 1e-9 >= b for a, b in zip(values, values[1:]))
        assert values[-1] == 0.0

    # 5. Attention hysteresis must suppress micro-churn while still allowing material danger.
    assert not should_switch(0.70, 0.72, 0.90, False)
    assert should_switch(0.70, 0.90, 0.90, False)
    assert should_switch(0.70, 0.71, 0.90, True)

    # 6. Fog lowers direct confidence; it cannot increase direct certainty.
    for _ in range(10000):
        distance = rng.uniform(0.0, 430.0)
        clear = direct_confidence(430.0, distance, 1.0)
        fog = direct_confidence(430.0, distance, rng.uniform(0.1, 0.6))
        assert fog <= clear + 1e-9

    # 7. Nerve changes ambiguity sensitivity but cannot create a threat from zero evidence.
    for nerve in (0.0, 0.5, 1.0):
        assert threat_score(0.0, 50.0, 180.0, 8.0, 1.0, 1.0, 1.0, nerve) == 0.0
        assert threat_score(0.8, 50.0, 180.0, 8.0, 1.0, 0.0, 1.0, nerve) == 0.0

    print("Perception R2 prediction audit passed: 5k threat permutations, 5k projectile permutations, 10k relay/fog cases, lost-track monotonicity and hysteresis invariants verified.")


if __name__ == "__main__":
    main()
