#!/usr/bin/env python3
from __future__ import annotations

from collections import Counter
from pathlib import Path
import math
import random

SOCIAL = Path('src/Creatures/DesertBatfly/Behavior/Social/DB_SocialRuntime.cs')
text = SOCIAL.read_text(encoding='utf-8')

required = (
    'internal enum DB_SocialNeedAxis',
    'AffiliationFatigue',
    'PlayFatigue',
    'GreetingFatigue',
    'RecentMode0',
    'RecentMode1',
    'RecentMode2',
    'ModeNoveltyScale(',
    'SocialSatisfactionFraction(',
    'DriveAfterInteraction(',
    'NeedFatigueGain(',
    'state.NegotiationCooldown = NegotiationCooldown(bat, completed);',
    'DB_SocialMode.PositionNegotiation => 0f',
    'PushHistory(state, mode, partnerKey);',
)
missing = [token for token in required if token not in text]
if missing:
    raise SystemExit('social-diversity contract missing: ' + ', '.join(missing))

# This model mirrors the pure scheduling/satiation math in DB_SocialRuntime. It deliberately
# does not simulate Unity flight geometry; it predicts the long-run mode mix after geometric
# opportunities exist, which is the monoculture risk this check is meant to catch.
def clamp01(x: float) -> float:
    return max(0.0, min(1.0, x))


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * clamp01(t)


def inverse_lerp(a: float, b: float, x: float) -> float:
    return 0.0 if b <= a else clamp01((x - a) / (b - a))


def start_threshold(c: float, t: float) -> float:
    value = lerp(0.68, 0.48, c) - inverse_lerp(0.75, 1.0, t) * 0.03
    return max(0.46, min(0.72, value))


def drive_per_tick(c: float, t: float) -> float:
    return 0.00230 * lerp(0.84, 1.24, c) * lerp(0.95, 1.07, t)


AXIS = {
    'companion': 'affiliation',
    'group': 'affiliation',
    'roost': 'affiliation',
    'chase': 'play',
    'pass': 'greeting',
}
RECOVERY = {'affiliation': 0.00155, 'play': 0.00135, 'greeting': 0.00220}


def freshness(fatigue: float) -> float:
    return lerp(1.0, 0.32, fatigue)


def novelty(mode: str, fatigue: dict[str, float], recent: list[str | None]) -> float:
    history = 0.35 if recent[0] == mode else 0.62 if recent[1] == mode else 0.80 if recent[2] == mode else 1.0
    return freshness(fatigue[AXIS[mode]]) * history


def satisfaction(mode: str, initiated: bool, drive: float) -> float:
    if mode == 'pass': return 0.16
    if mode == 'chase': return 0.42
    if mode == 'companion': return 0.50
    if mode == 'group' and initiated: return 0.62
    if mode == 'group': return lerp(0.24, 0.44, inverse_lerp(0.25, 0.75, drive))
    if mode == 'roost': return 0.46
    return 0.0


def fatigue_gain(mode: str, initiated: bool, drive: float) -> float:
    if mode == 'pass': return 0.42
    if mode == 'chase': return 0.62
    if mode == 'companion': return 0.55
    if mode == 'group' and initiated: return 0.72
    if mode == 'group': return lerp(0.38, 0.54, inverse_lerp(0.25, 0.75, drive))
    if mode == 'roost': return 0.48
    return 0.0


def drive_after(drive: float, mode: str, completed: bool = True, initiated: bool = True) -> float:
    sat = satisfaction(mode, initiated, drive) * (1.0 if completed else 0.22)
    return clamp01(drive * (1.0 - sat))


def base_weights(profile: tuple[float, float, float, float, float], drive: float,
                 available: dict[str, bool]) -> dict[str, float]:
    conformity, temperament, nerve, roost_affinity, bond = profile
    aggression_drive = inverse_lerp(0.52, 1.0, temperament)
    normalized_distance = 0.40
    companion = max(0.0,
        0.15 + conformity * 0.42 + (1.0 - temperament) * 0.22 + bond * 0.28 - normalized_distance * 0.18)
    companion *= (0.65 + drive * 0.55) * 0.85

    closing = 3.0
    pass_by = (0.30 + clamp01(closing / 6.0) * 0.45 + nerve * 0.16 +
               (1.0 - conformity) * 0.10) * 0.85

    chase = 0.0
    if temperament >= 0.50 and nerve >= 0.38:
        chase = (0.04 + aggression_drive * 0.62 + nerve * 0.22) * (0.55 + drive * 0.55) * 0.85

    group = 0.10 + conformity * 0.62 + (1.0 - temperament) * 0.20 + drive * 0.18
    roost = (0.08 + roost_affinity * 0.52 + conformity * 0.24 + (1.0 - temperament) * 0.10 +
             bond * 0.20 + 0.5 * 0.12) * (0.55 + drive * 0.60)

    weights = {'companion': companion, 'pass': pass_by, 'chase': chase, 'group': group, 'roost': roost}
    return {name: (value if available.get(name, True) else 0.0) for name, value in weights.items()}


AVG_COOLDOWN = {'companion': 140, 'pass': 90, 'chase': 215, 'group': 140, 'roost': 150}


def simulate(profile, events: int, opportunity_prob: dict[str, float], seed: int):
    rng = random.Random(seed)
    drive = 0.55
    fatigue = {'affiliation': 0.0, 'play': 0.0, 'greeting': 0.0}
    recent: list[str | None] = [None, None, None]
    counts: Counter[str] = Counter()
    same_mode = 0
    previous: str | None = None

    for _ in range(events):
        cooldown = 50 if previous is None else AVG_COOLDOWN[previous]
        drive_wait = max(0.0, (start_threshold(profile[0], profile[1]) - drive) /
                         max(1e-9, drive_per_tick(profile[0], profile[1])))
        wait = int(max(cooldown, drive_wait))
        drive = min(1.0, drive + drive_per_tick(profile[0], profile[1]) * wait)
        for axis_name in fatigue:
            fatigue[axis_name] = max(0.0, fatigue[axis_name] - RECOVERY[axis_name] * wait)

        available = {name: rng.random() < p for name, p in opportunity_prob.items()}
        weights = base_weights(profile, drive, available)
        for mode in weights:
            weights[mode] *= novelty(mode, fatigue, recent)
        total = sum(weights.values())
        if total <= 1e-9:
            previous = None
            continue

        pick = rng.random() * total
        selected = None
        for mode, weight in weights.items():
            pick -= weight
            if pick < 0.0:
                selected = mode
                break
        assert selected is not None

        if selected == previous:
            same_mode += 1
        counts[selected] += 1

        before = drive
        drive = drive_after(drive, selected, True, True)
        axis_name = AXIS[selected]
        fatigue[axis_name] = min(1.0, fatigue[axis_name] + fatigue_gain(selected, True, before))
        recent = [selected, recent[0], recent[1]]
        previous = selected

    total = sum(counts.values())
    shares = {mode: counts[mode] / total for mode in ('companion', 'pass', 'chase', 'group', 'roost')}
    return shares, same_mode / total


# Coordination does not consume social need. Short greeting contact is cheap, and a passive
# group participant must retain materially more drive than the group initiator.
assert math.isclose(drive_after(0.72, 'pass'), 0.6048, rel_tol=0.0, abs_tol=1e-6)
assert math.isclose(drive_after(0.72, 'negotiation'), 0.72, abs_tol=1e-9)
assert satisfaction('pass', True, 0.7) < satisfaction('chase', True, 0.7) < satisfaction('companion', True, 0.7)
assert satisfaction('group', False, 0.35) < satisfaction('group', True, 0.35)
assert drive_after(0.70, 'group', True, False) > drive_after(0.70, 'group', True, True)

# Repetition remains possible, but a just-completed mode is strongly discounted. The penalty
# fades over the three completed-event history and fatigue on one need axis does not suppress
# an unrelated need family.
f0 = {'affiliation': 0.0, 'play': 0.0, 'greeting': 0.0}
assert 0.30 <= novelty('companion', f0, ['companion', None, None]) <= 0.40
assert novelty('companion', f0, ['companion', None, None]) < novelty('companion', f0, [None, 'companion', None])
assert novelty('companion', f0, [None, 'companion', None]) < novelty('companion', f0, [None, None, 'companion'])
assert novelty('companion', {'affiliation': 1.0, 'play': 0.0, 'greeting': 0.0}, [None] * 3) < 0.35
assert math.isclose(novelty('chase', {'affiliation': 1.0, 'play': 0.0, 'greeting': 0.0}, [None] * 3), 1.0, abs_tol=1e-9)

profiles = {
    'average': (0.50, 0.50, 0.50, 0.50, 0.20),
    'affiliative_calm': (0.90, 0.25, 0.45, 0.65, 0.40),
    'playful_bold': (0.45, 0.90, 0.85, 0.40, 0.20),
    'roosty_social': (0.80, 0.35, 0.50, 0.95, 0.35),
}
all_available = {'companion': 1.0, 'pass': 1.0, 'chase': 1.0, 'group': 1.0, 'roost': 1.0}
dense_natural = {'companion': 1.0, 'pass': 0.30, 'chase': 1.0, 'group': 0.90, 'roost': 0.25}
sparse_natural = {'companion': 1.0, 'pass': 0.25, 'chase': 1.0, 'group': 0.25, 'roost': 0.15}

results = {}
for label, profile in profiles.items():
    shares, repeat = simulate(profile, 30000, all_available, 0xD35E + len(label))
    results[(label, 'all')] = shares
    assert max(shares.values()) < 0.36, (label, shares)
    assert repeat < 0.15, (label, repeat)

# Diversity balancing may not erase personality specialization.
assert results[('playful_bold', 'all')]['chase'] > results[('average', 'all')]['chase'] * 1.8
assert results[('roosty_social', 'all')]['roost'] > 0.20
assert (results[('affiliative_calm', 'all')]['companion'] +
        results[('affiliative_calm', 'all')]['group'] +
        results[('affiliative_calm', 'all')]['roost']) > 0.65

# If opportunities are sparse, specialization is allowed. The always-available CompanionDrift
# still must not crowd out every rarer event for a general individual.
avg_sparse, avg_sparse_repeat = simulate(profiles['average'], 30000, sparse_natural, 0x51A7)
assert avg_sparse['companion'] < 0.45, avg_sparse
assert avg_sparse['pass'] > 0.10 and avg_sparse['chase'] > 0.18, avg_sparse

aff_dense, _ = simulate(profiles['affiliative_calm'], 30000, dense_natural, 0x71B3)
assert max(aff_dense.values()) < 0.45, aff_dense
assert aff_dense['pass'] > 0.10, aff_dense

print('Social diversity prediction passed.')
for key in sorted(results):
    print(f'{key[0]:17s} all-available: ' + ', '.join(f'{m}={results[key][m]:.3f}' for m in results[key]))
print('average sparse-natural: ' + ', '.join(f'{m}={avg_sparse[m]:.3f}' for m in avg_sparse) +
      f'; immediate-repeat={avg_sparse_repeat:.3f}')
print('affiliative dense-natural: ' + ', '.join(f'{m}={aff_dense[m]:.3f}' for m in aff_dense))
