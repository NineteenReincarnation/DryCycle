from pathlib import Path

ROOT = Path('.')

def read(path): return (ROOT / path).read_text(encoding='utf-8')
def write(path, text):
    p = ROOT / path; p.parent.mkdir(parents=True, exist_ok=True); p.write_text(text, encoding='utf-8')
def replace_once(text, old, new, label):
    n = text.count(old)
    if n != 1: raise SystemExit(f'{label}: expected 1 match, found {n}')
    return text.replace(old, new, 1)

# -----------------------------------------------------------------------------
# Explicit Task13 cross-domain policy. No Reflection/RuntimeDetour and no state mutation.
# -----------------------------------------------------------------------------
policy_path = 'src/Creatures/DesertBatfly/Environmental/DB_EnvironmentalPolicy.cs'
write(policy_path, r'''using System;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Explicit R5 policy surface for Task13 soft modifiers consumed by other Desert Batfly
/// domains. This class never owns locomotion, never mutates Personality/Thirst to spoof a
/// decision, and never reaches another domain through Reflection or RuntimeDetour.
/// </summary>
internal static class DB_EnvironmentalPolicy
{
    private const float SandstormBlockerRadius = 120f;
    private const float SandstormReturningBatRadius = 340f;
    private const float MinimumActivityRangeScale = 0.28f;

    internal static bool AggressionAuthorized(DesertBatfly bat)
        => bat != null && (bat.Personality.Aggressive ||
            DesertBatflyEnvironmentalBehavior.AllowsEnvironmentalDamageAttack(bat));

    internal static float CombatMotivation(DesertBatfly bat)
    {
        if (bat == null) return 0f;
        float value = Mathf.Clamp01(bat.DesertState.Thirst);
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return value;

        if (influence.HarassMultiplier < 1f)
            value *= Mathf.Lerp(0.28f, 1f, influence.HarassMultiplier);
        if (influence.HeatAgitation > 0f && !influence.HardSurvival)
        {
            float heatMotivation = influence.HeatAgitation *
                (1f - influence.ThermalExhaustion) * 0.38f;
            value = Mathf.Max(value, bat.DesertState.Thirst + heatMotivation);
        }
        return Mathf.Clamp01(value);
    }

    internal static bool AllowsHarassCandidate(DesertBatfly bat, Creature creature)
    {
        if (bat == null || creature == null) return false;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return true;
        if (influence.HardSurvival) return false;

        float scale = influence.HarassMultiplier;
        if (creature is Player player && IsSandstormHomeAccessBlocker(bat, player, influence))
        {
            float defensiveDrive = Mathf.Clamp01(
                bat.Personality.Temperament * 0.55f +
                bat.Personality.Nerve * 0.25f +
                Mathf.Max(influence.HomeReturnDrive, influence.BurrowDrive) * 0.20f);
            scale = Mathf.Max(scale, Mathf.Lerp(0.62f, 0.86f, defensiveDrive));
        }

        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;
        int clockBucket = (bat.room?.game?.clock ?? 0) / 80;
        int targetKey = creature.abstractCreature?.ID.number ?? 0;
        return Stable01(bat.Personality.VisualSeed ^ targetKey * 397 ^ clockBucket * 7919) <= scale;
    }

    internal static float RoostChanceScale(DesertBatfly bat)
        => DesertBatflyEnvironmentalBehavior.RoostScale(bat);

    internal static int AdjustRoostDuration(DesertBatfly bat, int baseDuration)
    {
        if (bat == null || baseDuration <= 0 ||
            !DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return baseDuration;
        float scale = Mathf.Clamp(influence.RoostMultiplier, 1f, 4.5f);
        int adjusted = Mathf.RoundToInt(baseDuration * scale);
        if (DesertBatflyEnvironmentalBehavior.ShouldHoldEnvironmentalRoost(bat))
            adjusted = Mathf.Max(adjusted, influence.HardSurvival ? 2400 : 1200);
        return Mathf.Clamp(adjusted, baseDuration, 4200);
    }

    internal static bool BlocksNeutralSocial(DesertBatfly bat)
    {
        if (bat == null) return true;
        if (DesertBatflyEnvironmentalBehavior.SuppressNeutralSocial(bat)) return true;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                bat, out DesertBatflyEnvironmentalInfluence influence))
            return false;
        return ShouldSeekHome(influence) || ShouldBurrow(influence);
    }

    internal static float SocialDriveScale(DesertBatfly bat)
        => DesertBatflyEnvironmentalBehavior.SocialScale(bat);

    internal static float GroupCohesionScale(DesertBatfly bat)
        => DesertBatflyEnvironmentalBehavior.GroupCohesionScale(bat);

    internal static bool AllowsPlayChase(DesertBatfly bat)
    {
        if (bat == null) return false;
        float scale = DesertBatflyEnvironmentalBehavior.PlayScale(bat);
        if (scale >= 0.999f) return true;
        if (scale <= 0.001f) return false;
        int bucket = (bat.room?.game?.clock ?? 0) / 90;
        return Stable01(bat.Personality.VisualSeed ^ bucket * 0x632BE5AB) <= scale;
    }

    internal static bool WithinActivityRange(DesertBatfly source, DesertBatfly candidate, float baseRange)
    {
        if (source?.mainBodyChunk == null || candidate?.mainBodyChunk == null) return false;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(
                source, out DesertBatflyEnvironmentalInfluence influence))
            return Vector2.Distance(source.mainBodyChunk.pos, candidate.mainBodyChunk.pos) <= baseRange;
        float scale = Mathf.Clamp(influence.ActivityRadiusMultiplier, MinimumActivityRangeScale, 1f);
        return Vector2.Distance(source.mainBodyChunk.pos, candidate.mainBodyChunk.pos) <= baseRange * scale;
    }

    internal static bool ShouldSeekHome(in DesertBatflyEnvironmentalInfluence influence)
    {
        if (influence.HardSurvival && influence.HomeReturnDrive >= 0.35f) return true;
        if (influence.HomeReturnDrive < 0.58f) return false;
        if (influence.Weather is DesertBatflyEnvironmentalWeather.HeatWave or
            DesertBatflyEnvironmentalWeather.IntenseHeat)
        {
            float retreat = Mathf.Max(influence.HeatShelterDrive, influence.ThermalExhaustion);
            return retreat + 0.10f >= influence.HeatAgitation;
        }
        return true;
    }

    internal static bool ShouldBurrow(in DesertBatflyEnvironmentalInfluence influence)
    {
        if (influence.HardSurvival && influence.BurrowDrive >= 0.30f) return true;
        return influence.BurrowDrive >= 0.68f;
    }

    internal static float FogNavigationFamiliarityScale(
        DesertBatfly bat,
        DesertBatflyEnvironmentalWeather weather)
    {
        if (bat?.room?.abstractRoom == null ||
            weather is not (DesertBatflyEnvironmentalWeather.Fog or DesertBatflyEnvironmentalWeather.DenseFog))
            return 1f;

        DesertBatflyColonyRuntime.IndividualRecord record =
            DesertBatflyColonyRuntime.RecordFor(bat.abstractCreature, false);
        bool homeRoom = record != null && !string.IsNullOrEmpty(record.CurrentColony) &&
            string.Equals(record.CurrentColony, bat.room.abstractRoom.name, StringComparison.OrdinalIgnoreCase);
        if (!homeRoom) return 1f;

        bool nearHive = DesertBatflyEnvironmentalExposure.NearHive(
            bat.room, bat.room.GetTilePosition(bat.mainBodyChunk.pos), 10);
        if (weather == DesertBatflyEnvironmentalWeather.DenseFog)
            return nearHive ? 0.52f : 0.66f;
        return nearHive ? 0.72f : 0.82f;
    }

    internal static bool IsSandstormHomeAccessBlocker(
        DesertBatfly bat,
        Player player,
        in DesertBatflyEnvironmentalInfluence influence)
    {
        if (bat?.room == null || player?.mainBodyChunk == null || player.room != bat.room ||
            influence.HardSurvival ||
            influence.Weather is not (DesertBatflyEnvironmentalWeather.Sandstorm or
                DesertBatflyEnvironmentalWeather.DeathSandstorm) ||
            (influence.HomeReturnDrive < 0.45f && influence.BurrowDrive < 0.45f) ||
            bat.room.hives == null || bat.room.hives.Length == 0)
            return false;

        float playerRadiusSq = SandstormBlockerRadius * SandstormBlockerRadius;
        float batRadiusSq = SandstormReturningBatRadius * SandstormReturningBatRadius;
        Vector2 playerPos = player.mainBodyChunk.pos;
        Vector2 batPos = bat.mainBodyChunk.pos;
        for (int h = 0; h < bat.room.hives.Length; h++)
        {
            IntVector2[] hive = bat.room.hives[h];
            if (hive == null || hive.Length == 0) continue;
            for (int i = 0; i < hive.Length; i++)
            {
                Vector2 point = bat.room.MiddleOfTile(hive[i]);
                if ((playerPos - point).sqrMagnitude > playerRadiusSq) continue;
                if ((batPos - point).sqrMagnitude <= batRadiusSq) return true;
            }
        }
        return false;
    }

    private static float Stable01(int seed)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return (x & 0x00ffffffu) / 16777215f;
        }
    }
}
''')

# -----------------------------------------------------------------------------
# Combat consumes explicit environmental policy; no temporary Thirst spoofing.
# -----------------------------------------------------------------------------
combat_path = 'src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs'
combat = read(combat_path)
combat = combat.replace('fly.Personality.Aggressive', 'DB_EnvironmentalPolicy.AggressionAuthorized(fly)')
# Require at least the four expected eligibility sites to have migrated.
if combat.count('DB_EnvironmentalPolicy.AggressionAuthorized(fly)') < 4:
    raise SystemExit('combat aggression policy replacement unexpectedly incomplete')

old_motivated = '''        bool motivated = fly.DesertState.Thirst > observeThreshold * socialMotivationScale ||\n                         memory > 0 || rememberedCandidate != null;\n'''
new_motivated = '''        float combatMotivation = DB_EnvironmentalPolicy.CombatMotivation(fly);\n        bool motivated = combatMotivation > observeThreshold * socialMotivationScale ||\n                         memory > 0 || rememberedCandidate != null;\n'''
combat = replace_once(combat, old_motivated, new_motivated, 'combat scan motivation')

old_grief = '''    private bool GriefAllowsHarass() => !fly.Injury.BlocksCombat &&\n        (fly.DesertState.GriefStrength <= 0f ||\n         fly.DesertState.Thirst * fly.DesertState.GriefAttackScale >= DesertBatflyTuning.ObserveThirst) &&\n        (fly.Injury.AggressionScale >= 0.99f ||\n         fly.DesertState.Thirst * fly.Injury.AggressionScale >= DesertBatflyTuning.ObserveThirst);\n'''
new_grief = '''    private bool GriefAllowsHarass()\n    {\n        float motivation = DB_EnvironmentalPolicy.CombatMotivation(fly);\n        return !fly.Injury.BlocksCombat &&\n            (fly.DesertState.GriefStrength <= 0f ||\n             motivation * fly.DesertState.GriefAttackScale >= DesertBatflyTuning.ObserveThirst) &&\n            (fly.Injury.AggressionScale >= 0.99f ||\n             motivation * fly.Injury.AggressionScale >= DesertBatflyTuning.ObserveThirst);\n    }\n'''
combat = replace_once(combat, old_grief, new_grief, 'explicit environmental combat motivation')

old_canharass_tail = '''        return creature.TotalMass <= DesertBatflyTuning.LightTargetMass &&\n               relation.type != CreatureTemplate.Relationship.Type.Afraid &&\n               reverse.type != CreatureTemplate.Relationship.Type.Eats &&\n               reverse.type != CreatureTemplate.Relationship.Type.Attacks;\n'''
new_canharass_tail = '''        bool legal = creature.TotalMass <= DesertBatflyTuning.LightTargetMass &&\n                     relation.type != CreatureTemplate.Relationship.Type.Afraid &&\n                     reverse.type != CreatureTemplate.Relationship.Type.Eats &&\n                     reverse.type != CreatureTemplate.Relationship.Type.Attacks;\n        return legal && DB_EnvironmentalPolicy.AllowsHarassCandidate(fly, creature);\n'''
combat = replace_once(combat, old_canharass_tail, new_canharass_tail, 'environment Harass policy')

old_player_canharass = '''        if (creature is Player player) return !ai.IsTraumatizedPlayer(player);\n'''
new_player_canharass = '''        if (creature is Player player)\n            return !ai.IsTraumatizedPlayer(player) &&\n                   DB_EnvironmentalPolicy.AllowsHarassCandidate(fly, player);\n'''
combat = replace_once(combat, old_player_canharass, new_player_canharass, 'player Harass policy')

old_thirsty = '''                    bool thirsty = fly.DesertState.Thirst * fly.DesertState.GriefAttackScale *\n                                   fly.Injury.AggressionScale > effectiveAttackThirst;\n'''
new_thirsty = '''                    float combatMotivation = DB_EnvironmentalPolicy.CombatMotivation(fly);\n                    bool thirsty = combatMotivation * fly.DesertState.GriefAttackScale *\n                                   fly.Injury.AggressionScale > effectiveAttackThirst;\n'''
combat = replace_once(combat, old_thirsty, new_thirsty, 'combat execution motivation')
write(combat_path, combat)

# -----------------------------------------------------------------------------
# AI roost and remembered-player behavior consume policy directly.
# -----------------------------------------------------------------------------
ai_path = 'src/Creatures/DesertBatfly/DesertBatflyAI.cs'
ai = read(ai_path)
ai = replace_once(
    ai,
    '                if (remembered && fly.Personality.Aggressive)\n',
    '                if (remembered && DB_EnvironmentalPolicy.AggressionAuthorized(fly))\n',
    'remembered-player environmental aggression')
ai = replace_once(
    ai,
    '            fly.Personality.RoostChance * DesertBatflySocialBond.RoostScale(fly) * fly.Injury.RoostScale)\n',
    '            fly.Personality.RoostChance * DB_EnvironmentalPolicy.RoostChanceScale(fly) *\n            DesertBatflySocialBond.RoostScale(fly) * fly.Injury.RoostScale)\n',
    'roost chance explicit environment scale')
ai = replace_once(
    ai,
    '            if (!hasRoost || ticks > fly.Personality.RoostDuration || fly.AI.fleeFromRain)\n',
    '            int roostDuration = DB_EnvironmentalPolicy.AdjustRoostDuration(fly, fly.Personality.RoostDuration);\n            if (!hasRoost || ticks > roostDuration || fly.AI.fleeFromRain)\n',
    'roost duration explicit environment scale')
write(ai_path, ai)

# -----------------------------------------------------------------------------
# Social domain consumes Task13 soft modifiers directly.
# -----------------------------------------------------------------------------
social_path = 'src/Creatures/DesertBatfly/Social/DesertBatflySocialLife.cs'
social = read(social_path)
social = replace_once(
    social,
    '        state.Drive = Mathf.Clamp01(state.Drive + SocialDrivePerTick(bat.Personality) * traumaScale);\n',
    '        state.Drive = Mathf.Clamp01(state.Drive + SocialDrivePerTick(bat.Personality) * traumaScale *\n            DB_EnvironmentalPolicy.SocialDriveScale(bat));\n',
    'social drive explicit environment scale')
social = replace_once(
    social,
    '        if (bat.DesertAI == null || bat.AI == null) return "AI unavailable";\n',
    '        if (bat.DesertAI == null || bat.AI == null) return "AI unavailable";\n        if (DB_EnvironmentalPolicy.BlocksNeutralSocial(bat)) return "environmental survival priority";\n',
    'social environment priority gate')
social = replace_once(
    social,
    '                float joinPreference = GroupJoinPreference(other.Personality.Conformity, otherState.Drive);\n',
    '                float joinPreference = Mathf.Clamp01(\n                    GroupJoinPreference(other.Personality.Conformity, otherState.Drive) *\n                    DB_EnvironmentalPolicy.GroupCohesionScale(bat));\n',
    'group cohesion environment scale')
old_chase = '''    private static bool CanPlayChase(DesertBatfly bat)\n        => bat != null && !bat.Injury.IsSeverelyInjured && !bat.Injury.IsRecovering &&\n           bat.Injury.PostStunShock < 0.28f && bat.Injury.PhysicalCapability >= 0.72f;\n'''
new_chase = '''    private static bool CanPlayChase(DesertBatfly bat)\n        => bat != null && !bat.Injury.IsSeverelyInjured && !bat.Injury.IsRecovering &&\n           bat.Injury.PostStunShock < 0.28f && bat.Injury.PhysicalCapability >= 0.72f &&\n           DB_EnvironmentalPolicy.AllowsPlayChase(bat);\n'''
social = replace_once(social, old_chase, new_chase, 'play chase environment policy')
old_partner = '''        if (states.TryGetValue(candidate, out State candidateState) &&\n            (candidateState.Mode != DesertBatflySocialMode.None || candidateState.Cooldown > 0))\n            return false;\n        return SameRipple(source, candidate);\n'''
new_partner = '''        if (states.TryGetValue(candidate, out State candidateState) &&\n            (candidateState.Mode != DesertBatflySocialMode.None || candidateState.Cooldown > 0))\n            return false;\n        if (!DB_EnvironmentalPolicy.WithinActivityRange(source, candidate, SocialRange))\n            return false;\n        return SameRipple(source, candidate);\n'''
social = replace_once(social, old_partner, new_partner, 'social activity radius policy')
write(social_path, social)

# -----------------------------------------------------------------------------
# Fog familiarity mitigation moves out of the old Integration hook and into the R4 modifier.
# -----------------------------------------------------------------------------
fog_path = 'src/Creatures/DesertBatfly/Runtime/DB_FogGoalModifier.cs'
fog = read(fog_path)
old_radius = '''            float angle = Stable01(bat.Personality.VisualSeed ^ tick / 120) * Mathf.PI * 2f;\n            float radius = Mathf.Lerp(8f, 70f, influence.NavigationUncertainty);\n            state.Offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;\n'''
new_radius = '''            float angle = Stable01(bat.Personality.VisualSeed ^ tick / 120) * Mathf.PI * 2f;\n            float familiarity = DB_EnvironmentalPolicy.FogNavigationFamiliarityScale(bat, influence.Weather);\n            float uncertainty = Mathf.Clamp01(influence.NavigationUncertainty * familiarity);\n            float radius = Mathf.Lerp(5f, 62f, uncertainty);\n            state.Offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;\n'''
fog = replace_once(fog, old_radius, new_radius, 'fog familiarity policy')
old_fade = '''        float distance = Vector2.Distance(bat.mainBodyChunk.pos, goal);\n        float fade = Mathf.InverseLerp(70f, 320f, distance);\n        return goal + state.Offset * fade;\n'''
new_fade = '''        float distance = Vector2.Distance(bat.mainBodyChunk.pos, goal);\n        float anticipation = Mathf.Clamp(influence.ObstacleAnticipationScale, 0.30f, 1f);\n        float correctionStart = Mathf.Lerp(22f, 65f, anticipation);\n        float correctionFull = Mathf.Lerp(175f, 300f, anticipation);\n        float fade = Mathf.InverseLerp(correctionStart, correctionFull, distance);\n        return goal + state.Offset * fade;\n'''
fog = replace_once(fog, old_fade, new_fade, 'fog obstacle anticipation fade')
write(fog_path, fog)

# -----------------------------------------------------------------------------
# Survival bridge temporarily delegates shared policy helpers so B3 can remove the bridge
# without duplicating thresholds meanwhile.
# -----------------------------------------------------------------------------
survival_path = 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSurvivalBridge.cs'
survival = read(survival_path)
start = survival.index('    internal static bool ShouldSeekHome(in DesertBatflyEnvironmentalInfluence influence)')
end = survival.index('    private static void ApplySecondaryLightRainMoisture', start)
survival = survival[:start] + '''    internal static bool ShouldSeekHome(in DesertBatflyEnvironmentalInfluence influence)\n        => DB_EnvironmentalPolicy.ShouldSeekHome(influence);\n\n    internal static bool ShouldBurrow(in DesertBatflyEnvironmentalInfluence influence)\n        => DB_EnvironmentalPolicy.ShouldBurrow(influence);\n\n''' + survival[end:]
write(survival_path, survival)

# -----------------------------------------------------------------------------
# Lifecycle no longer installs EnvironmentalIntegration/SocialBridge; delete both files.
# -----------------------------------------------------------------------------
hooks_path = 'src/Creatures/DesertBatfly/DesertBatflyHooks.cs'
hooks = read(hooks_path)
hooks = replace_once(hooks, '        DesertBatflyEnvironmentalIntegration.Enable();\n', '', 'remove integration enable')
hooks = replace_once(hooks, '        DesertBatflyEnvironmentalIntegration.Disable();\n', '', 'remove integration disable')
hooks = replace_once(hooks, '        DesertBatflyEnvironmentalIntegration.Register(desert);\n', '', 'remove integration register')
write(hooks_path, hooks)

for path in [
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalIntegration.cs',
    'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSocialBridge.cs',
]:
    p = ROOT / path
    if not p.exists(): raise SystemExit(f'expected R5 B2 debt file missing: {path}')
    p.unlink()

# -----------------------------------------------------------------------------
# Update Task13 regression: protect direct policy behavior, not deleted architecture locks.
# -----------------------------------------------------------------------------
t13_path = 'tests/DesertBatfly/Program.Task13.cs'
t13 = read(t13_path)
old_dense = '''        Type denseFogBridge = mod.GetType(\n            "DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalDenseFogBridge", true);\n        Check((float)denseFogBridge.GetField("DenseFogShelterDemand", Flags).GetRawConstantValue() >= 0.50f &&\n              (float)denseFogBridge.GetField("DenseFogDisplacementStartIntensity", Flags).GetRawConstantValue() >= 0.80f,\n            "Task13 DenseFog only hands severe realized fog to Task09 temporary refuge semantics");\n        Check(denseFogBridge.GetMethod("SampleHook", Flags) != null &&\n              denseFogBridge.GetMethod("ShelterDemandHook", Flags) != null,\n            "Task13 DenseFog bridge modifies temporary shelter semantics without editing persistent migration memory");\n\n'''
new_dense = '''        Check(mod.GetType(\n                "DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalDenseFogBridge", false) == null,\n            "Task13 DenseFog behavior-neutral RuntimeDetour shim is retired in R5");\n\n'''
t13 = replace_once(t13, old_dense, new_dense, 'Task13 dense fog architecture guard')

old_social_block = '''        Type socialBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", true);\n        Type vengeanceBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", true);\n        Type visibilityPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);\n'''
new_social_block = '''        Type environmentalPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);\n        Type visibilityPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);\n'''
t13 = replace_once(t13, old_social_block, new_social_block, 'Task13 policy type migration')
old_vengeance_check = '''        Check(vengeanceBridge.GetMethod("UpdateHook", Flags) != null,\n            "Task13 hard survival suspends rather than clears Vengeance");\n\n        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", true);\n        Check(integration.GetMethod("CanHarassHook", Flags) != null &&\n              integration.GetMethod("SteerHook", Flags) != null,\n            "Task13 integrates environmental aggression and Fog navigation through existing DesertBatflyAI gates");\n        Check(integration.GetMethod("FogNavigationFamiliarityScale", Flags) != null,\n            "Task13 DenseFog navigation has an explicit Home/Hive familiarity mitigation path");\n\n'''
new_vengeance_check = '''        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", false) == null,\n            "Task13 R5 retires internal Environment RuntimeDetour integration layers");\n        Check(environmentalPolicy.GetMethod("AggressionAuthorized", Flags) != null &&\n              environmentalPolicy.GetMethod("CombatMotivation", Flags) != null &&\n              environmentalPolicy.GetMethod("AllowsHarassCandidate", Flags) != null &&\n              environmentalPolicy.GetMethod("AdjustRoostDuration", Flags) != null &&\n              environmentalPolicy.GetMethod("BlocksNeutralSocial", Flags) != null &&\n              environmentalPolicy.GetMethod("FogNavigationFamiliarityScale", Flags) != null,\n            "Task13 environmental effects are consumed through an explicit cross-domain policy API");\n\n'''
t13 = replace_once(t13, old_vengeance_check, new_vengeance_check, 'Task13 detour-to-policy checks')
old_lifecycle = '''        Check(MethodCallOffset(hooksEnable, denseFogBridge, "Enable") >= 0 &&\n              MethodCallOffset(hooksEnable, task09Bridge, "Enable") >= 0 &&\n              MethodCallOffset(hooksEnable, survivalBridge, "Enable") >= 0,\n            "Task13 DenseFog, Task09 and native survival bridges are wired into DesertBatfly lifecycle");\n        Check(MethodCallOffset(hooksDisable, denseFogBridge, "Disable") >= 0 &&\n              MethodCallOffset(hooksDisable, task09Bridge, "Disable") >= 0 &&\n              MethodCallOffset(hooksDisable, survivalBridge, "Disable") >= 0,\n            "Task13 auxiliary bridges are removed with DesertBatfly lifecycle");\n'''
new_lifecycle = '''        Check(MethodCallOffset(hooksEnable, task09Bridge, "Enable") >= 0 &&\n              MethodCallOffset(hooksEnable, survivalBridge, "Enable") >= 0,\n            "Task13 remaining Task09/native-survival adapters are wired into DesertBatfly lifecycle");\n        Check(MethodCallOffset(hooksDisable, task09Bridge, "Disable") >= 0 &&\n              MethodCallOffset(hooksDisable, survivalBridge, "Disable") >= 0,\n            "Task13 remaining auxiliary adapters are removed with DesertBatfly lifecycle");\n'''
t13 = replace_once(t13, old_lifecycle, new_lifecycle, 'Task13 lifecycle bridge migration')
old_forbidden_list = '''                     behavior, roomRuntime, profile, integration, socialBridge,\n                     vengeanceBridge, visibilityPolicy, weaponPerception, denseFogBridge,\n                     task09Bridge, survivalBridge,\n'''
new_forbidden_list = '''                     behavior, roomRuntime, profile, environmentalPolicy,\n                     visibilityPolicy, weaponPerception, task09Bridge, survivalBridge,\n'''
t13 = replace_once(t13, old_forbidden_list, new_forbidden_list, 'Task13 forbidden type list')
write(t13_path, t13)

# -----------------------------------------------------------------------------
# Strengthen R5 regression and status.
# -----------------------------------------------------------------------------
r5_path = 'tests/DesertBatfly/Program.Task14R5.cs'
r5 = read(r5_path)
r5 = replace_once(
    r5,
    '''        Check(integration.GetField("steerHook", Flags) != null &&\n              integration.GetField("scanCreaturesHook", Flags) != null,\n            "R5 B1 intentionally leaves EnvironmentalIntegration debt visible for the next migration batch");\n\n        Console.WriteLine("Task14 R5 B1: DenseFog and Vengeance bridge shims are physically removed; Threat Vengeance uses a direct tactical API.");\n''',
    '''        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", false) == null,\n            "R5 removes Environment Reflection/RuntimeDetour integration and social bridge");\n        Type policy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EnvironmentalPolicy", true);\n        Check(policy.GetMethod("AggressionAuthorized", Flags) != null &&\n              policy.GetMethod("CombatMotivation", Flags) != null &&\n              policy.GetMethod("AllowsHarassCandidate", Flags) != null &&\n              policy.GetMethod("AdjustRoostDuration", Flags) != null &&\n              policy.GetMethod("BlocksNeutralSocial", Flags) != null,\n            "R5 explicit environmental policy replaces mutation/detour based cross-domain behavior");\n\n        Console.WriteLine("Task14 R5 B2: Environment Combat/Roost/Social integration is explicit; old detours and temporary Thirst spoofing are removed.");\n''',
    'extend R5 managed guard')
# Remove now-invalid integration type declaration.
r5 = r5.replace('        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration", true);\n', '')
write(r5_path, r5)

status_path = 'docs/Discussion/Task_14_R5_BridgeDebtStatus.txt'
status = read(status_path)
status = status.replace('Revision: R5-B1 / 2026-09-07', 'Revision: R5-B2 / 2026-09-07')
status = status.replace('Status: 【R5 已正式启动 / B1 obsolete bridge removal implemented】',
                        'Status: 【R5 进行中 / B2 Environmental detour debt removed】')
status += '''\n\n======================================================================\n5. R5-B2 closed\n======================================================================\n\n- DesertBatflyEnvironmentalIntegration.cs DELETED.\n- DesertBatflyEnvironmentalSocialBridge.cs DELETED.\n- Added DB_EnvironmentalPolicy as an explicit read-only cross-domain policy surface.\n- Combat uses explicit AggressionAuthorized / CombatMotivation / AllowsHarassCandidate.\n- HB-06 CLOSED code-side: no temporary DesertBatflyState.Thirst mutation is used to spoof\n  combat motivation.\n- AI Roost chance/duration directly consumes environmental scale.\n- Social drive, Group cohesion, Play chase and activity radius directly consume Task13.\n- Fog Home/Hive familiarity mitigation moved into DB_FogGoalModifier through policy; no\n  environmental Steer RuntimeDetour remains.\n- Task13 tests now protect policy behavior and physical absence of deleted bridges rather\n  than old Architecture Locks.\n\nRemaining R5 bridge debt is Task09/Survival and Signal bridge families plus RuntimePatch\nclassification.\n'''
write(status_path, status)

# Source-level guard: HB-06 and detour layers must be physically absent.
assert not (ROOT / 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalIntegration.cs').exists()
assert not (ROOT / 'src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSocialBridge.cs').exists()
assert 'MonoMod.RuntimeDetour' not in read(policy_path)
assert 'DesertState.Thirst =' not in read(policy_path)
assert 'DB_EnvironmentalPolicy.CombatMotivation(fly)' in read(combat_path)
assert 'DB_EnvironmentalPolicy.SocialDriveScale(bat)' in read(social_path)
assert 'DB_EnvironmentalPolicy.RoostChanceScale(fly)' in read(ai_path)
assert 'DB_EnvironmentalPolicy.FogNavigationFamiliarityScale' in read(fog_path)
assert 'DesertBatflyEnvironmentalIntegration' not in read(hooks_path)

# Clean migration machinery.
this = ROOT / 'scripts/task14_r5_b2_apply.py'
if this.exists(): this.unlink()
wf = ROOT / '.github/workflows/task14-r5-b2.yml'
if wf.exists(): wf.unlink()
print('R5 B2 environmental policy migration prepared successfully')
