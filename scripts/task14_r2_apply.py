from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


def replace_block(text, start, end, replacement, label):
    i = text.find(start)
    if i < 0:
        raise RuntimeError(f"{label}: start marker not found")
    j = text.find(end, i)
    if j < 0:
        raise RuntimeError(f"{label}: end marker not found")
    return text[:i] + replacement + text[j:]


# -----------------------------------------------------------------------------
# DesertBatflyAI: candidate lists and weapon observations come from DB_RoomContext.
# -----------------------------------------------------------------------------
ai_path = "src/Creatures/DesertBatfly/DesertBatflyAI.cs"
ai = read(ai_path)

ai = replace_once(
    ai,
    """        if (!fly.room.VisualContact(\n                fly.mainBodyChunk.pos,\n                Target.mainBodyChunk.pos))\n            unseen++;\n        else\n            unseen = 0;\n""",
    """        DB_VisibilityChannel targetChannel = Target is Player\n            ? DB_VisibilityChannel.Player\n            : DB_VisibilityChannel.Creature;\n        if (!DB_VisibilityPolicy.CanObserve(\n                fly,\n                Target.mainBodyChunk.pos,\n                430f,\n                targetChannel))\n            unseen++;\n        else\n            unseen = 0;\n""",
    "AI current-target visibility")

ai = replace_block(
    ai,
    "    private bool AcquireSlot()\n",
    "    private bool Valid(Creature creature)\n",
    """    private bool AcquireSlot()\n    {\n        if (fly.Injury.BlocksCombat) { hasSlot = false; return false; }\n        if (Target is Player player && IsTraumatizedPlayer(player))\n        {\n            hasSlot = false;\n            return false;\n        }\n\n        int count = 0;\n        DB_RoomContext context = DB_RoomContext.For(fly.room);\n        var bats = context?.Bats;\n        if (bats != null)\n        {\n            for (int i = 0; i < bats.Count; i++)\n            {\n                DesertBatfly other = bats[i];\n                if (other == null || other == fly || !other.Consious ||\n                    other.grabbedBy.Count != 0 ||\n                    other.DesertAI.Target != Target || !other.DesertAI.FormalAttack)\n                    continue;\n                count++;\n            }\n        }\n\n        hasSlot = count < DesertBatflyTuning.AttackSlots;\n        return hasSlot;\n    }\n\n""",
    "AI AttackSlots RoomContext migration")

ai = replace_block(
    ai,
    "    private void ScanCreatures()\n",
    "    private Player FindSocialHarassTarget()\n",
    """    private void ScanCreatures()\n    {\n        danger = null;\n        Creature candidate = null;\n        Player rememberedCandidate = null;\n        float closest = DesertBatflyTuning.SightRange;\n\n        DB_RoomContext context = DB_RoomContext.For(fly.room);\n        var creatures = context?.Creatures;\n        if (creatures == null) return;\n\n        for (int i = 0; i < creatures.Count; i++)\n        {\n            Creature creature = creatures[i];\n            if (creature == fly || creature is DesertBatfly || !Valid(creature))\n                continue;\n\n            float distance = Vector2.Distance(\n                fly.mainBodyChunk.pos,\n                creature.mainBodyChunk.pos);\n            DB_VisibilityChannel channel = creature is Player\n                ? DB_VisibilityChannel.Player\n                : DB_VisibilityChannel.Creature;\n            if (distance > DesertBatflyTuning.SightRange ||\n                !DB_VisibilityPolicy.CanObserve(\n                    fly,\n                    creature.mainBodyChunk.pos,\n                    DesertBatflyTuning.SightRange,\n                    channel))\n                continue;\n\n            CreatureTemplate.Relationship relation =\n                fly.Template.CreatureRelationship(creature.Template);\n            CreatureTemplate.Relationship reverse =\n                creature.Template.CreatureRelationship(fly.Template);\n            bool predator = creature is not Player &&\n                (relation.type == CreatureTemplate.Relationship.Type.Afraid ||\n                 reverse.type == CreatureTemplate.Relationship.Type.Eats ||\n                 reverse.type == CreatureTemplate.Relationship.Type.Attacks);\n\n            if (predator)\n            {\n                float ordinaryThreatDistance = Mathf.Lerp(\n                    90f,\n                    260f,\n                    Mathf.Clamp01(creature.TotalMass));\n                float nerveScale = Mathf.Lerp(\n                    1.15f,\n                    0.58f,\n                    fly.Personality.Nerve);\n                float threatDistance = Mathf.Max(\n                    55f,\n                    ordinaryThreatDistance * nerveScale);\n                if (distance < threatDistance)\n                    danger = creature;\n            }\n\n            if (creature is Player player)\n            {\n                bool traumatized = IsTraumatizedPlayer(player);\n                bool remembered = !traumatized && IsRememberedPlayer(player);\n                if (remembered)\n                {\n                    if (fly.Personality.Aggressive)\n                    {\n                        rememberedCandidate = player;\n                    }\n                    else\n                    {\n                        float fearDistance = Mathf.Lerp(\n                            DesertBatflyTuning.GrabFearMinDistance,\n                            DesertBatflyTuning.GrabFearMaxDistance,\n                            fly.DesertState.GrabMemoryStrength);\n                        fearDistance *= Mathf.Lerp(\n                            1.12f,\n                            0.72f,\n                            fly.Personality.Nerve);\n                        if (distance < fearDistance)\n                            danger = player;\n                    }\n                }\n\n                float reactionDistance = Mathf.Lerp(\n                    125f,\n                    78f,\n                    fly.Personality.Nerve);\n                float closingThreshold = Mathf.Lerp(\n                    2.1f,\n                    4.4f,\n                    fly.Personality.Nerve);\n                int pursuitThreshold = Mathf.RoundToInt(Mathf.Lerp(\n                    16f,\n                    44f,\n                    fly.Personality.Nerve));\n\n                if (remembered && fly.Personality.Aggressive)\n                {\n                    reactionDistance *= 0.72f;\n                    closingThreshold *= 1.25f;\n                    pursuitThreshold = Mathf.RoundToInt(\n                        pursuitThreshold * 1.35f);\n                }\n\n                if (distance < reactionDistance)\n                {\n                    float closing = Vector2.Dot(\n                        player.mainBodyChunk.vel,\n                        Custom.DirVec(\n                            player.mainBodyChunk.pos,\n                            fly.mainBodyChunk.pos));\n                    if (closing > closingThreshold)\n                        pursuit += 8;\n                    else\n                        pursuit = Mathf.Max(0, pursuit - 4);\n\n                    if (pursuit >= pursuitThreshold)\n                    {\n                        DisturbedByApproach(player);\n                        pursuit = 0;\n                    }\n                }\n                else\n                {\n                    pursuit = Mathf.Max(0, pursuit - 2);\n                }\n            }\n\n            if (distance < closest && CanHarass(creature))\n            {\n                closest = distance;\n                candidate = creature;\n            }\n        }\n\n        if (Target != null || !fly.Personality.Aggressive || retreat > 0)\n            return;\n\n        bool retaliationPending = retaliationCharges > 0 &&\n                                  retaliationRecovery <= 0;\n        if (fly.DesertState.Cooldown > 0 && !retaliationPending)\n            return;\n\n        if (!GriefAllowsHarass()) return;\n        Player socialCandidate = FindSocialHarassTarget();\n        float observeThreshold = Mathf.Lerp(\n            DesertBatflyTuning.ObserveThirst,\n            0.18f,\n            fly.Personality.AggressionDrive * 0.45f);\n\n        float socialMotivationScale = socialCandidate != null\n            ? Mathf.Lerp(1f, 0.72f, fly.Personality.Conformity)\n            : 1f;\n        bool motivated = fly.DesertState.Thirst >\n                          observeThreshold * socialMotivationScale ||\n                          memory > 0 || rememberedCandidate != null;\n        if (!motivated) return;\n\n        if (Valid(attacker) && CanHarass(attacker))\n            Target = attacker;\n        else if (rememberedCandidate != null)\n            Target = rememberedCandidate;\n        else if (socialCandidate != null)\n            Target = socialCandidate;\n        else\n            Target = candidate;\n\n        if (Target != null)\n            SetMode(Activity.Observe);\n    }\n\n""",
    "AI creature candidate RoomContext migration")

ai = replace_block(
    ai,
    "    private Player FindSocialHarassTarget()\n",
    "    private void ScanWeapons()\n",
    """    private Player FindSocialHarassTarget()\n    {\n        if (fly.Personality.Conformity < 0.42f || fly.room == null)\n            return null;\n\n        float socialDrive =\n            fly.Personality.Conformity * 0.55f +\n            fly.Personality.AggressionDrive * 0.25f +\n            fly.Personality.Nerve * 0.20f;\n        if (socialDrive < 0.52f) return null;\n\n        DB_RoomContext context = DB_RoomContext.For(fly.room);\n        var bats = context?.Bats;\n        if (bats == null) return null;\n\n        Player best = null;\n        float bestScore = float.MinValue;\n        for (int i = 0; i < bats.Count; i++)\n        {\n            DesertBatfly bat = bats[i];\n            if (bat == null || bat == fly || !bat.Consious ||\n                bat.DesertAI.Target is not Player target ||\n                !CanHarass(target))\n                continue;\n\n            if (bat.DesertAI.Mode is not (\n                Activity.Observe or Activity.Approach or Activity.Circle or\n                Activity.FakeDive or Activity.Dive))\n                continue;\n\n            float neighbourDistance = Vector2.Distance(\n                fly.mainBodyChunk.pos,\n                bat.mainBodyChunk.pos);\n            if (neighbourDistance > 210f ||\n                (neighbourDistance > 95f &&\n                 !DB_VisibilityPolicy.CanObserve(\n                     fly,\n                     bat.mainBodyChunk.pos,\n                     210f,\n                     DB_VisibilityChannel.Social)))\n                continue;\n\n            float score =\n                socialDrive * 1.2f -\n                neighbourDistance / 420f +\n                bat.Personality.AggressionDrive * 0.18f;\n            if (score <= bestScore) continue;\n            bestScore = score;\n            best = target;\n        }\n        return best;\n    }\n\n""",
    "AI social candidate RoomContext migration")

ai = replace_block(
    ai,
    "    private void ScanWeapons()\n",
    "    private bool IsRememberedPlayer(Player player)\n",
    """    private void ScanWeapons()\n    {\n        if (!DB_WeaponPerception.TryFindImmediateThreat(\n                fly,\n                out DB_WeaponObservation observation))\n            return;\n\n        if (observation.Instigator != null)\n            Threatened(observation.Instigator, false);\n    }\n\n""",
    "AI weapon RoomContext migration")

if "fly.room.physicalObjects" in ai or "foreach (AbstractCreature abs in fly.room.abstractRoom.creatures)" in ai:
    raise RuntimeError("AI still contains direct room scanner after R2 migration")
write(ai_path, ai)


# -----------------------------------------------------------------------------
# ThreatRuntime: RoomState keeps only Threat temporal evidence; observations use DB context.
# -----------------------------------------------------------------------------
threat_path = "src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs"
threat = read(threat_path)

threat = replace_block(
    threat,
    "    private sealed class RoomState\n",
    "    private sealed class ProcessedExplosion\n",
    """    private sealed class RoomState\n    {\n        // Threat-owned temporal evidence only. Players/weapons belong to DB_RoomContext.\n        internal readonly int[] RecentSpearThrow = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };\n        internal readonly int[] RecentRockThrow = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };\n        internal readonly int[] RecentExplosion = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };\n        internal readonly int[] RecentGrab = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };\n        internal readonly int[] CasualtyWindowStart = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };\n        internal readonly int[] CasualtyCount = new int[4];\n\n        internal void RecordThrow(Weapon weapon, Player player, int clock)\n        {\n            int slot = PlayerSlot(player);\n            if (!ValidSlot(slot) || weapon == null) return;\n            if (weapon is Spear) RecentSpearThrow[slot] = clock;\n            if (weapon is Rock) RecentRockThrow[slot] = clock;\n        }\n\n        internal void RecordExplosion(Player player, int clock)\n        {\n            int slot = PlayerSlot(player);\n            if (ValidSlot(slot)) RecentExplosion[slot] = clock;\n        }\n\n        internal void RecordGrab(Player player, int clock)\n        {\n            int slot = PlayerSlot(player);\n            if (ValidSlot(slot)) RecentGrab[slot] = clock;\n        }\n\n        internal bool RecordCasualty(Player player, int clock)\n        {\n            int slot = PlayerSlot(player);\n            if (!ValidSlot(slot)) return false;\n            if (CasualtyWindowStart[slot] == int.MinValue || clock < CasualtyWindowStart[slot] ||\n                clock - CasualtyWindowStart[slot] > 320)\n            {\n                CasualtyWindowStart[slot] = clock;\n                CasualtyCount[slot] = 1;\n                return false;\n            }\n            CasualtyCount[slot]++;\n            return CasualtyCount[slot] >= 2;\n        }\n    }\n\n""",
    "Threat RoomState observation removal")

threat = replace_block(
    threat,
    "    private static void UpdateCue(DesertBatfly bat, RuntimeState state)\n",
    "    private static void ApplyHeldThreatPriority(DesertBatfly bat, RuntimeState state)\n",
    """    private static void UpdateCue(DesertBatfly bat, RuntimeState state)\n    {\n        if (state.CueRefresh > 0)\n        {\n            state.CueRefresh--;\n            return;\n        }\n        state.CueRefresh = CueRefreshTicks;\n\n        Room room = bat.room;\n        RoomState roomState = RoomFor(room);\n        DB_RoomContext context = DB_RoomContext.For(room);\n        if (context == null)\n        {\n            state.Cue = default;\n            return;\n        }\n\n        Player player = bat.DesertAI.Target as Player;\n        if (player == null || player.dead || player.room != room ||\n            !DB_VisibilityPolicy.CanObserve(\n                bat, player.mainBodyChunk.pos, 430f, DB_VisibilityChannel.Player))\n            player = NearestVisiblePlayer(bat, context.Players);\n\n        DesertBatflyThreatCue cue = default;\n        cue.PlayerSlot = PlayerSlot(player);\n        if (player == null || !ValidSlot(cue.PlayerSlot))\n        {\n            state.Cue = cue;\n            return;\n        }\n\n        if (DB_WeaponPerception.TryObserveHeldThreats(\n                bat, player, 430f, out DB_HeldThreatObservation held))\n        {\n            cue.VisibleSpear = held.VisibleSpear;\n            cue.VisibleRock = held.VisibleRock;\n            cue.VisibleExplosive = held.VisibleExplosive;\n            cue.VisibleStartle = held.VisibleStartle;\n            cue.VisibleShock = held.VisibleShock;\n        }\n\n        int clock = room.game?.clock ?? 0;\n        cue.RecentSpearThrow = Recent(roomState.RecentSpearThrow[cue.PlayerSlot], clock, ProjectileCueTicks);\n        cue.RecentRockThrow = Recent(roomState.RecentRockThrow[cue.PlayerSlot], clock, ProjectileCueTicks);\n        cue.RecentExplosion = Recent(roomState.RecentExplosion[cue.PlayerSlot], clock, ExplosionCueTicks);\n        cue.RecentGrabAttempt = Recent(roomState.RecentGrab[cue.PlayerSlot], clock, GrabCueTicks);\n        cue.CurrentHazardCenter = state.HazardTimer > 0 ? state.HazardCenter : null;\n        cue.PlayerRetreating = state.EncounterPlayerSlot == cue.PlayerSlot && state.RetreatTicks > 12;\n\n        if (DB_WeaponPerception.TryFindIncomingProjectileFrom(\n                bat,\n                player,\n                ProjectileNearMissMaxDistance,\n                ProjectileNearMissRadius,\n                16f,\n                out DB_WeaponObservation projectile))\n        {\n            cue.ProjectileThreat = true;\n            cue.ProjectileThreatDirection = projectile.Velocity.normalized;\n            LearnNearMiss(bat, state, projectile.Weapon, player, clock);\n        }\n\n        state.Cue = cue;\n        if (cue.ProjectileThreat)\n        {\n            DesertBatflySocialLife.CancelForPriority(bat, "Task11 incoming projectile");\n            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.Threatened(player, false);\n        }\n    }\n\n""",
    "Threat current cue RoomContext migration")

threat = replace_block(
    threat,
    "    private static void ApplyHeldThreatPriority(DesertBatfly bat, RuntimeState state)\n",
    "    private static void LearnNearMiss(\n",
    """    private static void ApplyHeldThreatPriority(DesertBatfly bat, RuntimeState state)\n    {\n        DesertBatflyThreatCue cue = state.Cue;\n        if (!ValidSlot(cue.PlayerSlot) || bat.room == null) return;\n        DesertBatflyPlayerThreatMemory memory =\n            DesertBatflyThreatMemoryStore.For(bat.DesertState, cue.PlayerSlot);\n        if (memory == null || memory.Confidence < 0.08f) return;\n\n        Player player = DB_RoomContext.For(bat.room)?.PlayerBySlot(cue.PlayerSlot);\n        if (player == null) return;\n\n        float heldRisk = 0f;\n        if (cue.VisibleSpear) heldRisk += memory.PiercingPressure * 0.48f;\n        if (cue.VisibleRock) heldRisk += memory.BluntStunPressure * 0.24f;\n        if (cue.VisibleExplosive) heldRisk += memory.ExplosionPressure * 0.65f;\n        if (cue.VisibleStartle) heldRisk += memory.StartlePressure * 0.46f;\n        if (cue.VisibleShock) heldRisk += memory.ShockPressure * 0.50f;\n        heldRisk *= Mathf.Lerp(1.15f, 0.72f, bat.Personality.Nerve);\n        heldRisk *= Mathf.Lerp(0.70f, 1f, memory.Confidence);\n        heldRisk = Mathf.Clamp01(heldRisk);\n        if (heldRisk < 0.22f) return;\n\n        float distance = Vector2.Distance(bat.mainBodyChunk.pos, player.mainBodyChunk.pos);\n        if (distance < Mathf.Lerp(170f, 260f, heldRisk))\n            DesertBatflySocialLife.CancelForPriority(bat, "Task11 learned held-item caution");\n\n        if (heldRisk >= 0.72f && distance < 155f &&\n            !DesertBatflyIntimidation.IsExtremeVengeanceActive(bat) &&\n            bat.DesertAI.Target == null)\n        {\n            bat.DesertAI.Threatened(player, false);\n            state.ModifierReason = "recognized currently held threat";\n        }\n    }\n\n""",
    "Threat held priority RoomContext migration")

threat = replace_block(
    threat,
    "    private static void TrackPursuit(DesertBatfly bat, RuntimeState state)\n",
    "    private static void ExtendLearnedDisengage(DesertBatfly bat, RuntimeState state)\n",
    """    private static void TrackPursuit(DesertBatfly bat, RuntimeState state)\n    {\n        if (bat.DesertAI.Mode != DesertBatflyAI.Activity.Escape || bat.room == null)\n        {\n            state.PursuitTicks = 0;\n            state.PursuitLastDistance = -1f;\n            state.PursuitAwarded = false;\n            state.PursuitPlayerSlot = -1;\n            return;\n        }\n\n        if (state.PreviousMode != DesertBatflyAI.Activity.Escape)\n            state.PursuitDisengageExtended = false;\n\n        DB_RoomContext context = DB_RoomContext.For(bat.room);\n        Player player = context != null\n            ? NearestVisiblePlayer(bat, context.Players, 300f)\n            : null;\n        if (player == null)\n        {\n            state.PursuitTicks = 0;\n            state.PursuitLastDistance = -1f;\n            return;\n        }\n\n        int slot = PlayerSlot(player);\n        state.EscapeThreatPlayerSlot = slot;\n        float distance = Vector2.Distance(player.mainBodyChunk.pos, bat.mainBodyChunk.pos);\n        float closing = Vector2.Dot(\n            player.mainBodyChunk.vel,\n            Custom.DirVec(player.mainBodyChunk.pos, bat.mainBodyChunk.pos));\n        bool continuing = state.PursuitPlayerSlot == slot && state.PursuitLastDistance >= 0f &&\n            distance < state.PursuitLastDistance - 0.35f && closing > 1.25f;\n        if (continuing) state.PursuitTicks++;\n        else if (closing > 1.6f) state.PursuitTicks = Mathf.Max(1, state.PursuitTicks - 1);\n        else state.PursuitTicks = Mathf.Max(0, state.PursuitTicks - 3);\n        state.PursuitPlayerSlot = slot;\n        state.PursuitLastDistance = distance;\n\n        if (!state.PursuitAwarded && state.PursuitTicks >= PursuitMinimumTicks)\n        {\n            AddEvidence(\n                bat,\n                player,\n                DesertBatflyThreatAdapterRegistry.PursuitEvidence(),\n                1f,\n                "sustained pursuit",\n                false);\n            state.PursuitAwarded = true;\n        }\n    }\n\n""",
    "Threat pursuit RoomContext migration")

threat = replace_block(
    threat,
    "    private static void ExtendLearnedDisengage(DesertBatfly bat, RuntimeState state)\n",
    "    private static void TrackEncounter(DesertBatfly bat, RuntimeState state)\n",
    """    private static void ExtendLearnedDisengage(DesertBatfly bat, RuntimeState state)\n    {\n        if (state.PreviousMode != DesertBatflyAI.Activity.Escape ||\n            bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape ||\n            state.PursuitDisengageExtended || !ValidSlot(state.EscapeThreatPlayerSlot))\n            return;\n\n        DesertBatflyPlayerThreatMemory memory = DesertBatflyThreatMemoryStore.For(\n            bat.DesertState, state.EscapeThreatPlayerSlot);\n        if (memory == null || memory.PursuitPressure < 0.26f) return;\n\n        Player player = DB_RoomContext.For(bat.room)?.PlayerBySlot(state.EscapeThreatPlayerSlot);\n        if (player == null || !Custom.DistLess(\n                bat.mainBodyChunk.pos,\n                player.mainBodyChunk.pos,\n                Mathf.Lerp(190f, 300f, memory.PursuitPressure)))\n            return;\n\n        state.PursuitDisengageExtended = true;\n        DesertBatflySocialLife.CancelForPriority(bat, "Task11 learned pursuit disengage");\n        bat.DesertAI.Threatened(player, false);\n        state.ModifierReason = "learned pursuer: extended disengage";\n    }\n\n""",
    "Threat disengage RoomContext migration")

threat = replace_block(
    threat,
    "    private static void TrackEncounter(DesertBatfly bat, RuntimeState state)\n",
    "    private static void ResetEncounter(RuntimeState state)\n",
    """    private static void TrackEncounter(DesertBatfly bat, RuntimeState state)\n    {\n        if (bat.DesertAI.Target is not Player player || bat.room == null ||\n            bat.DesertAI.Mode is DesertBatflyAI.Activity.Escape or DesertBatflyAI.Activity.Attach or\n                DesertBatflyAI.Activity.Interfere or DesertBatflyAI.Activity.RetaliationCharge)\n        {\n            ResetEncounter(state);\n            return;\n        }\n\n        int slot = PlayerSlot(player);\n        if (!ValidSlot(slot) ||\n            !DB_VisibilityPolicy.CanObserve(\n                bat, player.mainBodyChunk.pos, 360f, DB_VisibilityChannel.Player))\n        {\n            ResetEncounter(state);\n            return;\n        }\n\n        float distance = Vector2.Distance(bat.mainBodyChunk.pos, player.mainBodyChunk.pos);\n        if (distance > 360f)\n        {\n            ResetEncounter(state);\n            return;\n        }\n\n        if (state.EncounterPlayerSlot != slot)\n        {\n            ResetEncounter(state);\n            state.EncounterPlayerSlot = slot;\n            state.EncounterLastDistance = distance;\n            return;\n        }\n\n        RoomState roomState = RoomFor(bat.room);\n        int clock = bat.room.game?.clock ?? 0;\n        bool playerRecentlyAttacked =\n            Recent(roomState.RecentSpearThrow[slot], clock, 120) ||\n            Recent(roomState.RecentRockThrow[slot], clock, 120) ||\n            Recent(roomState.RecentExplosion[slot], clock, 160) ||\n            Recent(roomState.RecentGrab[slot], clock, 120) ||\n            (state.RecentDamagePlayerSlot == slot && Recent(state.RecentDamageTick, clock, 160));\n\n        state.EncounterTicks++;\n        if (!playerRecentlyAttacked && state.EncounterLastDistance >= 0f &&\n            distance > state.EncounterLastDistance + 0.35f)\n            state.RetreatTicks++;\n        else\n            state.RetreatTicks = Mathf.Max(0, state.RetreatTicks - 2);\n        state.EncounterLastDistance = distance;\n\n        if (!state.RetreatAwarded && state.RetreatTicks >= RetreatMinimumTicks)\n        {\n            AddEvidence(\n                bat,\n                player,\n                DesertBatflyThreatAdapterRegistry.RetreatEvidence(),\n                1f,\n                "encounter retreat",\n                false);\n            state.RetreatAwarded = true;\n        }\n\n        if (!playerRecentlyAttacked &&\n            state.EncounterTicks >= NonAggressionEncounterTicks * (state.NonAggressionAwards + 1) &&\n            state.NonAggressionAwards < 3)\n        {\n            AddEvidence(\n                bat,\n                player,\n                DesertBatflyThreatAdapterRegistry.NonAggressionEvidence(),\n                1f,\n                "non-aggressive encounter",\n                false);\n            state.NonAggressionAwards++;\n        }\n    }\n\n""",
    "Threat encounter visibility migration")

threat = replace_block(
    threat,
    "    private static Player PlayerBySlot(List<Player> players, int slot)\n",
    "    private static Player NearestVisiblePlayer(\n",
    "",
    "Threat local player lookup removal")

threat = replace_block(
    threat,
    "    private static Player NearestVisiblePlayer(\n",
    "    private static RuntimeState StateFor(DesertBatfly bat)\n",
    """    private static Player NearestVisiblePlayer(\n        DesertBatfly bat,\n        IReadOnlyList<Player> players,\n        float maxDistance = 430f)\n    {\n        if (bat == null || players == null) return null;\n        Player best = null;\n        float bestDistance = maxDistance;\n        for (int i = 0; i < players.Count; i++)\n        {\n            Player player = players[i];\n            if (player == null || player.dead || player.room != bat.room) continue;\n            float distance = Vector2.Distance(bat.mainBodyChunk.pos, player.mainBodyChunk.pos);\n            if (distance >= bestDistance ||\n                !DB_VisibilityPolicy.CanObserve(\n                    bat, player.mainBodyChunk.pos, maxDistance, DB_VisibilityChannel.Player))\n                continue;\n            bestDistance = distance;\n            best = player;\n        }\n        return best;\n    }\n\n""",
    "Threat nearest-player visibility migration")

threat = replace_once(
    threat,
    """            if (player != null && evidence.Any &&\n                room.VisualContact(bat.mainBodyChunk.pos, explosion.pos))\n""",
    """            if (player != null && evidence.Any &&\n                DB_VisibilityPolicy.CanObserve(\n                    bat, explosion.pos, acuteRadius, DB_VisibilityChannel.Signal))\n""",
    "Threat explosion visual witness")
threat = replace_once(
    threat,
    """            if (player != null && room.VisualContact(bat.mainBodyChunk.pos, position))\n""",
    """            if (player != null && DB_VisibilityPolicy.CanObserve(\n                    bat, position, acuteRadius, DB_VisibilityChannel.Signal))\n""",
    "Threat startle visual witness")
threat = replace_once(
    threat,
    """            if (distance > WitnessFarRadius ||\n                !room.VisualContact(witness.mainBodyChunk.pos, threatEvent.Position))\n                continue;\n""",
    """            if (distance > WitnessFarRadius ||\n                !DB_VisibilityPolicy.CanObserve(\n                    witness, threatEvent.Position, WitnessFarRadius, DB_VisibilityChannel.Signal))\n                continue;\n""",
    "Threat witness visibility")

if "roomState.Refresh" in threat or "internal readonly List<Player> Players" in threat or "internal readonly List<Weapon> ThrownWeapons" in threat:
    raise RuntimeError("ThreatRuntime still owns room observation scanner")
if "room.physicalObjects" in threat:
    raise RuntimeError("ThreatRuntime still scans physicalObjects directly")
write(threat_path, threat)


# -----------------------------------------------------------------------------
# Signals: visibility is now first-class; the old environmental signal detour is obsolete.
# -----------------------------------------------------------------------------
signal_path = "src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs"
signal = read(signal_path)
signal = replace_block(
    signal,
    "    private static bool TryPerceive(\n",
    "    private static float ResponseStrength(\n",
    """    private static bool TryPerceive(\n        DesertBatfly receiver,\n        DesertBatflySignalPacket packet,\n        out DesertBatflySignalPerception perception,\n        out float attenuation)\n    {\n        perception = DesertBatflySignalPerception.None;\n        attenuation = 0f;\n        float distance = Vector2.Distance(receiver.mainBodyChunk.pos, packet.Emitter.mainBodyChunk.pos);\n        float baseVisualRadius = VisualRadius(packet.Kind);\n        float visibility = DesertBatflyEnvironmentalBehavior.VisibilityScale(receiver);\n        float visualRadius = DB_VisibilityPolicy.EffectiveRange(\n            baseVisualRadius, visibility, DB_VisibilityChannel.Signal);\n\n        if (distance <= visualRadius && DB_VisibilityPolicy.CanObserve(\n                receiver,\n                packet.Emitter.mainBodyChunk.pos,\n                baseVisualRadius,\n                DB_VisibilityChannel.Signal))\n        {\n            perception = DesertBatflySignalPerception.Visual;\n            attenuation = Mathf.Lerp(1f, 0.34f, Mathf.Clamp01(distance / Mathf.Max(1f, visualRadius)));\n            return true;\n        }\n\n        float acousticRadius = packet.Kind switch\n        {\n            DesertBatflySignalKind.AlarmFlutter => 95f,\n            DesertBatflySignalKind.DistressCall => 108f,\n            _ => 0f\n        };\n        if (acousticRadius <= 0f || distance > acousticRadius) return false;\n\n        perception = DesertBatflySignalPerception.CloseAcoustic;\n        attenuation = Mathf.Lerp(0.62f, 0.30f, Mathf.Clamp01(distance / acousticRadius));\n        return true;\n    }\n\n    internal static float VisualRadius(DesertBatflySignalKind kind) => kind switch\n    {\n        DesertBatflySignalKind.AlarmFlutter => 300f,\n        DesertBatflySignalKind.DistressCall => 250f,\n        DesertBatflySignalKind.RallySignal => 235f,\n        DesertBatflySignalKind.RoostCall => 215f,\n        DesertBatflySignalKind.HarassSignal => 235f,\n        DesertBatflySignalKind.SafeSignal => 195f,\n        _ => 200f\n    };\n\n""",
    "Signal visibility policy migration")
write(signal_path, signal)


# -----------------------------------------------------------------------------
# Environmental integration no longer owns visual signal/threat detours.
# -----------------------------------------------------------------------------
integration_path = "src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalIntegration.cs"
integration = read(integration_path)
integration = replace_once(
    integration,
    "        DesertBatflyEnvironmentalSignalBridge.Enable();\n        DesertBatflyEnvironmentalThreatBridge.Enable();\n",
    "",
    "Environmental visual bridge enable removal")
integration = replace_once(
    integration,
    "        DesertBatflyEnvironmentalThreatBridge.Disable();\n        DesertBatflyEnvironmentalSignalBridge.Disable();\n",
    "",
    "Environmental visual bridge disable removal")
integration = replace_once(
    integration,
    """\n        if (influence.VisibilityConfidence >= 0.98f || ai.Target == null || ai.FormalAttack)\n            return;\n\n        float visibleRange = DesertBatflyTuning.SightRange * Mathf.Lerp(0.42f, 1f, influence.VisibilityConfidence);\n        if (Vector2.Distance(bat.mainBodyChunk.pos, ai.Target.mainBodyChunk.pos) > visibleRange &&\n            ai.Mode == DesertBatflyAI.Activity.Observe)\n            ai.CancelAttack();\n""",
    "\n",
    "Environmental duplicate AI visibility removal")
write(integration_path, integration)


# -----------------------------------------------------------------------------
# Room hook: DesertSwarmRoom keeps its global spawning/hive responsibility, but expensive
# DesertBatfly-only Refuge/Signal/Environment room work is lazy and bat-driven.
# -----------------------------------------------------------------------------
hooks_path = "src/Creatures/DesertBatfly/DesertBatflyHooks.cs"
hooks = read(hooks_path)
hooks = replace_block(
    hooks,
    "    private static void UpdateRoom(On.Room.orig_Update orig, Room self)\n",
    "    private static int Nourishment(\n",
    """    private static void UpdateRoom(On.Room.orig_Update orig, Room self)\n    {\n        orig(self);\n\n        // DesertSwarmRoom still owns room-tag/hive spawning semantics and is intentionally\n        // not gated by realized bats. The remaining systems are DesertBatfly-only and must\n        // not allocate/scan ordinary rooms that never activated DB_RoomContext.\n        DesertSwarmRoom.UpdateRoom(self, self.game.evenUpdate);\n        if (!DB_RoomContext.TryGetExisting(self, out DB_RoomContext context) ||\n            context.Bats.Count == 0)\n            return;\n\n        if (self.readyForAI && self.aimap != null)\n            DesertBatflyRefuge.ObserveRoom(self);\n        DesertBatflySignalRoomRuntime.For(self)?.Prune(self);\n        DesertBatflyEnvironmentalRoomRuntime.Update(self);\n    }\n\n""",
    "Room lazy DesertBatfly runtime activation")
write(hooks_path, hooks)


# -----------------------------------------------------------------------------
# Task13 regression: the two visual bridges are intentionally gone, replaced by R2 policy.
# -----------------------------------------------------------------------------
t13_path = "tests/DesertBatfly/Program.Task13.cs"
t13 = read(t13_path)
t13 = replace_block(
    t13,
    "        Type socialBridge = mod.GetType(\"DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge\", true);\n",
    "        Type integration = mod.GetType(\"DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalIntegration\", true);\n",
    """        Type socialBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSocialBridge", true);\n        Type vengeanceBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalVengeanceBridge", true);\n        Type visibilityPolicy = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VisibilityPolicy", true);\n        Type weaponPerception = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_WeaponPerception", true);\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSignalBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalThreatBridge", false) == null,\n            "Task13 visual signal/threat bridges are retired after R2 central perception adoption");\n        Check(visibilityPolicy.GetMethod("CanObserve", Flags) != null &&\n              weaponPerception.GetMethod("TryFindIncomingProjectileFrom", Flags) != null,\n            "Task13 Fog/DenseFog visibility and close projectile recognition now use shared R2 perception policy");\n        Check(vengeanceBridge.GetMethod("UpdateHook", Flags) != null,\n            "Task13 hard survival suspends rather than clears Vengeance");\n\n""",
    "Task13 visual bridge architecture-lock migration")
t13 = replace_once(
    t13,
    """                     behavior, roomRuntime, profile, integration, socialBridge, signalBridge,\n                     threatBridge, vengeanceBridge, denseFogBridge, task09Bridge, survivalBridge,\n                     mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalExposure", true)\n""",
    """                     behavior, roomRuntime, profile, integration, socialBridge,\n                     vengeanceBridge, visibilityPolicy, weaponPerception, denseFogBridge,\n                     task09Bridge, survivalBridge,\n                     mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalExposure", true)\n""",
    "Task13 forbidden-type list migration")
write(t13_path, t13)


# Obsolete R2 visual bridge files are removed after consumers moved to the shared policy.
for obsolete in [
    "src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalSignalBridge.cs",
    "src/Creatures/DesertBatfly/Environmental/DesertBatflyEnvironmentalThreatBridge.cs",
]:
    p = ROOT / obsolete
    if not p.exists():
        raise RuntimeError(f"obsolete bridge missing before planned deletion: {obsolete}")
    p.unlink()


# Final structural guards for the one-shot patch.
assert "DB_RoomContext" in read(ai_path)
assert "DB_WeaponPerception" in read(ai_path)
assert "DB_VisibilityPolicy" in read(ai_path)
assert "room.physicalObjects" not in read(ai_path)
assert "roomState.Refresh" not in read(threat_path)
assert "DB_WeaponPerception.TryFindIncomingProjectileFrom" in read(threat_path)
assert "DB_VisibilityPolicy" in read(threat_path)
assert "DB_VisibilityPolicy" in read(signal_path)
assert "DesertBatflyEnvironmentalSignalBridge" not in read(integration_path)
assert "DesertBatflyEnvironmentalThreatBridge" not in read(integration_path)
assert "DB_RoomContext.TryGetExisting" in read(hooks_path)

# Remove one-shot machinery from the resulting tree. The running workflow already has the
# script loaded, so deleting these files here is safe and prevents permanent CI clutter.
for transient in [
    "scripts/task14_r2_apply.py",
    ".github/workflows/task14-r2-one-shot.yml",
]:
    p = ROOT / transient
    if p.exists():
        p.unlink()

print("Task14 R2 one-shot source migration completed successfully.")
