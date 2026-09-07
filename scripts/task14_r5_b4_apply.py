from pathlib import Path

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{path}: expected one match, found {count}: {old[:120]!r}')
    write(path, text.replace(old, new, 1))


# -----------------------------------------------------------------------------
# 1. Lifecycle: Task12 no longer installs internal RuntimeDetour bridges.
# -----------------------------------------------------------------------------
hooks = 'src/Creatures/DesertBatfly/DesertBatflyHooks.cs'
replace_once(hooks,
'''        DesertBatflySignalIntegration.Enable();\n        DesertBatflySignalVengeanceBridge.Enable();\n''', '')
replace_once(hooks,
'''        DesertBatflySignalVengeanceBridge.Disable();\n        DesertBatflySignalIntegration.Disable();\n''', '')


# -----------------------------------------------------------------------------
# 2. AI owns explicit escape facts and emits Task12 alarms without reflection.
# -----------------------------------------------------------------------------
ai = 'src/Creatures/DesertBatfly/DesertBatflyAI.cs'
old_threatened = '''    internal void Threatened(Creature source, bool directAttack = false)\n    {\n        ClearRecoveryNavigation();\n        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "immediate threat / escape");\n        if (source != null && source != fly && source is not DesertBatfly)\n        {\n            combat.RecordAttacker(source, directAttack ? 1f : 0f);\n            escapeFrom = source.mainBodyChunk.pos;\n        }\n        else\n        {\n            escapeFrom = fly.mainBodyChunk.pos - Vector2.up * 20f;\n        }\n\n        if (IsInFlyChain(fly))\n            BreakHangChain(source, DesertBatflyTuning.RetreatTicks);\n        else\n        {\n            retreat = DesertBatflyTuning.RetreatTicks;\n            CancelAttack();\n            SetMode(Activity.Escape);\n        }\n        RaiseLocalAlarm();\n    }\n'''
new_threatened = '''    internal void Threatened(\n        Creature source,\n        bool directAttack = false,\n        bool emitAlarm = true)\n    {\n        Vector2 origin = source?.mainBodyChunk != null\n            ? source.mainBodyChunk.pos\n            : fly.mainBodyChunk.pos - Vector2.up * 20f;\n        ThreatenedAt(source, origin, directAttack, emitAlarm);\n    }\n\n    internal void ThreatenedAt(\n        Creature source,\n        Vector2 origin,\n        bool directAttack = false,\n        bool emitAlarm = true)\n    {\n        ClearRecoveryNavigation();\n        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "immediate threat / escape");\n        if (source != null && source != fly && source is not DesertBatfly)\n            combat.RecordAttacker(source, directAttack ? 1f : 0f);\n        escapeFrom = origin;\n\n        if (IsInFlyChain(fly))\n            BreakHangChain(source, DesertBatflyTuning.RetreatTicks);\n        else\n        {\n            retreat = DesertBatflyTuning.RetreatTicks;\n            CancelAttack();\n            SetMode(Activity.Escape);\n        }\n\n        if (emitAlarm)\n            RaiseLocalAlarm(source, origin,\n                source == null ? "direct anonymous danger -> Task12 AlarmFlutter" :\n                    "direct threat -> Task12 AlarmFlutter");\n    }\n'''
replace_once(ai, old_threatened, new_threatened)
replace_once(ai,
'''        RaiseLocalAlarm();\n    }\n\n    internal void PlayerReleased''',
'''        RaiseLocalAlarm(player, player.mainBodyChunk.pos,\n            "direct player grab -> Task12 AlarmFlutter");\n    }\n\n    internal void PlayerReleased''')
old_alarm = '''    private void RaiseLocalAlarm()\n    {\n        if (fly.room == null) return;\n        foreach (Fly other in DesertSwarmRoom.For(fly.room).Hive.flies)\n        {\n            if (other is not DesertBatfly bat || bat == fly || bat.dead || bat.slatedForDeletetion ||\n                bat.room != fly.room || bat.inShortcut ||\n                !Custom.DistLess(\n                    fly.mainBodyChunk.pos,\n                    bat.mainBodyChunk.pos,\n                    DesertBatflyTuning.AlarmRadius))\n                continue;\n\n            bat.DesertAI.escapeFrom = escapeFrom;\n            bat.DesertAI.retreat = Mathf.Max(bat.DesertAI.retreat, 25);\n        }\n    }\n'''
new_alarm = '''    private void RaiseLocalAlarm(Creature threat, Vector2 origin, string reason)\n    {\n        if (fly.room == null) return;\n        Vector2 direction = Custom.DirVec(fly.mainBodyChunk.pos, origin);\n        DesertBatflySignalRuntime.EmitAlarm(\n            fly,\n            threat,\n            origin,\n            direction,\n            Mathf.Lerp(0.58f, 0.92f, 1f - fly.Personality.Nerve),\n            reason);\n    }\n'''
replace_once(ai, old_alarm, new_alarm)


# -----------------------------------------------------------------------------
# 3. SignalRuntime directly owns response modulation, alarm application and emissions.
# -----------------------------------------------------------------------------
sig = 'src/Creatures/DesertBatfly/Signals/DesertBatflySignalRuntime.cs'
replace_once(sig,
'''        DesertBatflySignalRoomRuntime.Reset();\n        DesertBatflySignalIntegration.Reset();\n''',
'''        DesertBatflySignalRoomRuntime.Reset();\n''')
replace_once(sig,
'''                DesertBatflySignalIntegration.ApplyAlarm(receiver, packet, response);\n''',
'''                ApplyAlarm(receiver, packet, response);\n''')
replace_once(sig,
'''        if (DesertBatflySignalIntegration.IsVengeanceAvenger(bat))\n        {\n            Creature target = DesertBatflySignalIntegration.VengeanceTarget(bat);\n''',
'''        if (DesertBatflyIntimidation.IsVengeanceAvenger(bat))\n        {\n            DesertBatflyIntimidation.TryGetVengeanceTarget(bat, out Creature target);\n''')

emit_methods = r'''
    internal static DesertBatflySignalPacket EmitRally(
        DesertBatfly emitter,
        Creature threat,
        float drive,
        string reason)
    {
        if (!Available(emitter) || threat == null) return null;
        Vector2 origin = emitter.mainBodyChunk.pos;
        Vector2 direction = threat.mainBodyChunk != null
            ? Custom.DirVec(origin, threat.mainBodyChunk.pos)
            : Vector2.zero;
        DesertBatflySignalRoomRuntime.RoomState room = DesertBatflySignalRoomRuntime.For(emitter.room);
        DesertBatflySignalPacket packet = room?.AddOrRefresh(
            emitter.room,
            DesertBatflySignalKind.RallySignal,
            emitter,
            emitter,
            threat,
            threat as Player,
            origin,
            direction,
            Mathf.Clamp01(0.55f + Mathf.Clamp01(drive) * 0.30f),
            84);
        if (packet == null) return null;

        SetDisplay(emitter, DesertBatflySignalKind.RallySignal, packet.Intensity, 42, direction);
        room.DeliverUrgent(emitter.room, packet);
        TraceEmit(emitter, packet, reason);
        return packet;
    }

    internal static DesertBatflySignalPacket EmitAcuteAlarm(
        Room room,
        Creature threat,
        Vector2 position,
        float intensity,
        string reason)
    {
        DesertBatfly emitter = FindAcuteEmitter(room, position);
        if (emitter == null) return null;
        Vector2 direction = Custom.DirVec(emitter.mainBodyChunk.pos, position);
        return EmitAlarm(emitter, threat, position, direction, intensity, reason);
    }

'''
replace_once(sig,
'''    internal static DesertBatflySignalPacket EmitDistress(\n''',
emit_methods + '''    internal static DesertBatflySignalPacket EmitDistress(\n''')

apply_alarm = r'''
    private static void ApplyAlarm(
        DesertBatfly receiver,
        DesertBatflySignalPacket packet,
        float response)
    {
        if (receiver == null || packet == null || response < 0.30f || receiver.dead ||
            !receiver.Consious || receiver.room == null || receiver.inShortcut ||
            receiver.Injury.IsSeverelyInjured ||
            DesertBatflyTravelNavigation.HasIntent(receiver.abstractCreature))
            return;

        Creature threat = packet.Threat;
        if (threat != null && (threat.dead || threat.room != receiver.room))
            return;

        // Signal perception may create a short Escape fact, but it never rebroadcasts a new
        // root from ThreatenedAt. Relays remain owned solely by SignalRoomRuntime.
        receiver.DesertAI.ThreatenedAt(threat, packet.Origin, false, false);
    }

    private static DesertBatfly FindAcuteEmitter(Room room, Vector2 position)
    {
        if (room == null) return null;
        DesertBatfly best = null;
        float bestScore = float.MaxValue;
        foreach (Fly member in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (member is not DesertBatfly bat || bat.dead || bat.slatedForDeletetion ||
                !bat.Consious || bat.room != room || bat.inShortcut)
                continue;

            float distance = Vector2.Distance(bat.mainBodyChunk.pos, position);
            if (distance > 480f) continue;
            bool visual = room.VisualContact(bat.mainBodyChunk.pos, position);
            float score = distance + (visual ? 0f : 95f);
            if (score >= bestScore) continue;
            bestScore = score;
            best = bat;
        }
        return best;
    }

'''
replace_once(sig,
'''    internal static bool TryGetInfluence(DesertBatfly bat, out DesertBatflySignalInfluence influence)\n''',
apply_alarm + '''    internal static bool TryGetInfluence(DesertBatfly bat, out DesertBatflySignalInfluence influence)\n''')

old_response_return = '''        };\n        return Mathf.Clamp01(packet.Intensity * attenuation * scale);\n    }\n\n    private static bool ShouldRelayAlarm'''
new_response_return = '''        };\n        float response = Mathf.Clamp01(packet.Intensity * attenuation * scale);\n\n        // Task11 memory stays private to the receiver. Task12 reads it only to modulate the\n        // receiver's own willingness; no emitter memory/evidence is copied through a signal.\n        Player player = packet.PlayerTarget ?? packet.Threat as Player;\n        if (player != null)\n        {\n            int slot = DesertBatflyThreatRuntime.PlayerSlot(player);\n            if (DesertBatflyThreatRuntime.ValidSlot(slot))\n            {\n                DesertBatflyPlayerThreatMemory memory =\n                    DesertBatflyThreatMemoryStore.For(receiver.DesertState, slot);\n                if (memory != null && memory.Confidence >= 0.04f)\n                {\n                    float lethalCaution = Mathf.Clamp01(\n                        memory.PiercingPressure * 0.30f +\n                        memory.CounterKillPressure * 0.34f +\n                        memory.ExplosionPressure * 0.17f +\n                        memory.GrabCapturePressure * 0.10f +\n                        memory.PursuitPressure * 0.09f);\n                    float caution = lethalCaution * memory.Confidence;\n                    response *= packet.Kind switch\n                    {\n                        DesertBatflySignalKind.AlarmFlutter => 1f + caution * 0.24f,\n                        DesertBatflySignalKind.DistressCall => 1f - caution * 0.22f,\n                        DesertBatflySignalKind.RallySignal => 1f - caution * 0.52f,\n                        DesertBatflySignalKind.HarassSignal => 1f - caution * 0.62f,\n                        _ => 1f\n                    };\n                }\n            }\n        }\n\n        return Mathf.Clamp01(response);\n    }\n\n    private static bool ShouldRelayAlarm'''
replace_once(sig, old_response_return, new_response_return)


# -----------------------------------------------------------------------------
# 4. Semantic capture consumer owns capture signals directly.
# -----------------------------------------------------------------------------
consumers = 'src/Creatures/DesertBatfly/Runtime/DB_EventConsumers.cs'
replace_once(consumers,
'''using UnityEngine;\n''',
'''using RWCustom;\nusing UnityEngine;\n''')
replace_once(consumers,
'''        DesertBatflySocialLife.CancelForPriority(victim, "semantic capture event");\n\n        if (capture.Captor is Lizard predator &&\n''',
'''        DesertBatflySocialLife.CancelForPriority(victim, "semantic capture event");\n\n        Creature signalThreat = capture.Captor;\n        if (signalThreat != null && signalThreat.room == victim.room)\n        {\n            bool tongueSignal = capture.CaptureKind == DB_CaptureKind.Tongue;\n            DesertBatflySignalRuntime.EmitDistress(\n                victim,\n                signalThreat,\n                tongueSignal ? 0.94f : 0.88f,\n                tongueSignal\n                    ? "semantic tongue capture emits one DistressCall"\n                    : "semantic non-Fly grasp emits one DistressCall");\n            Vector2 signalOrigin = signalThreat.mainBodyChunk?.pos ?? capture.Position;\n            DesertBatflySignalRuntime.EmitAlarm(\n                victim,\n                signalThreat,\n                signalOrigin,\n                Custom.DirVec(victim.mainBodyChunk.pos, signalOrigin),\n                tongueSignal ? 0.90f : 0.82f,\n                tongueSignal\n                    ? "semantic tongue capture emits one AlarmFlutter"\n                    : "semantic grasp emits one AlarmFlutter alongside DistressCall");\n        }\n\n        if (capture.Captor is Lizard predator &&\n''')
replace_once(consumers,
'''                victim.DesertAI.Threatened(predator, true);\n''',
'''                victim.DesertAI.Threatened(predator, true, false);\n''')


# -----------------------------------------------------------------------------
# 5. Combat/Social consume Harass/Roost signal influence directly.
# -----------------------------------------------------------------------------
combat = 'src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs'
old_social_target = '''    private Player FindSocialHarassTarget()\n    {\n        if (fly.Personality.Conformity < 0.42f || fly.room == null) return null;\n        float socialDrive = fly.Personality.Conformity * 0.55f +\n                            fly.Personality.AggressionDrive * 0.25f +\n                            fly.Personality.Nerve * 0.20f;\n        if (socialDrive < 0.52f) return null;\n\n        DB_RoomContext context = DB_RoomContext.For(fly.room);\n        var bats = context?.Bats;\n        if (bats == null) return null;\n\n        Player best = null;\n        float bestScore = float.MinValue;\n        for (int i = 0; i < bats.Count; i++)\n        {\n            DesertBatfly bat = bats[i];\n            if (bat == null || bat == fly || !bat.Consious ||\n                bat.DesertAI.Target is not Player otherTarget || !CanHarass(otherTarget))\n                continue;\n            if (bat.DesertAI.Mode is not (\n                DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or\n                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or\n                DesertBatflyAI.Activity.Dive))\n                continue;\n\n            float neighbourDistance = Vector2.Distance(fly.mainBodyChunk.pos, bat.mainBodyChunk.pos);\n            if (neighbourDistance > 210f ||\n                (neighbourDistance > 95f && !DB_VisibilityPolicy.CanObserve(\n                    fly, bat.mainBodyChunk.pos, 210f, DB_VisibilityChannel.Social)))\n                continue;\n\n            float score = socialDrive * 1.2f - neighbourDistance / 420f +\n                          bat.Personality.AggressionDrive * 0.18f;\n            if (score <= bestScore) continue;\n            bestScore = score;\n            best = otherTarget;\n        }\n        return best;\n    }\n'''
new_social_target = '''    private Player FindSocialHarassTarget()\n    {\n        if (fly.room == null || fly.Injury.BlocksCombat ||\n            DesertBatflyIntimidation.HasActiveFearSuppression(fly) ||\n            !DesertBatflySignalRuntime.TryGetInfluence(fly, out DesertBatflySignalInfluence influence) ||\n            influence.HarassInterest < 0.20f)\n            return null;\n\n        Player target = influence.HarassTarget;\n        if (target == null || target.dead || target.room != fly.room || !CanHarass(target) ||\n            !DB_VisibilityPolicy.CanObserve(\n                fly, target.mainBodyChunk.pos, DesertBatflyTuning.SightRange, DB_VisibilityChannel.Player))\n            return null;\n\n        if (DesertBatflyThreatRuntime.TryGetDebugState(fly, out DesertBatflyThreatDebugState threat))\n        {\n            float caution = threat.CounterKillPressure * 0.55f +\n                            threat.PiercingPressure * 0.30f +\n                            threat.GrabCapturePressure * 0.15f;\n            float courage = fly.Personality.Nerve * 0.55f +\n                            fly.Personality.Temperament * 0.45f;\n            if (caution * threat.Confidence > courage + 0.18f)\n                return null;\n        }\n\n        return target;\n    }\n'''
replace_once(combat, old_social_target, new_social_target)

social = 'src/Creatures/DesertBatfly/Social/DesertBatflySocialLife.cs'
start = read(social).index('    private static DesertBatfly FindRoostSource(')
end = read(social).index('    private static bool ValidRoostSource(', start)
text = read(social)
new_roost = '''    private static DesertBatfly FindRoostSource(\n        DesertBatfly bat,\n        IReadOnlyList<DesertBatfly> roosting,\n        out int chainSize,\n        out float bond)\n    {\n        chainSize = 0;\n        bond = 0f;\n        if (bat == null || bat.room == null ||\n            !DesertBatflySignalRuntime.TryGetInfluence(bat, out DesertBatflySignalInfluence influence) ||\n            influence.RoostInterest < 0.16f)\n            return null;\n\n        DesertBatfly source = influence.RoostSource;\n        if (!ValidRoostSource(source, bat) ||\n            Vector2.Distance(bat.mainBodyChunk.pos, source.mainBodyChunk.pos) > 230f)\n            return null;\n\n        chainSize = ChainLength(source);\n        bond = Mathf.Max(\n            DesertBatflySocialBond.GetBondStrength(bat, source),\n            DesertBatflySocialBond.GetBondStrength(source, bat));\n        return source;\n    }\n\n'''
write(social, text[:start] + new_roost + text[end:])


# -----------------------------------------------------------------------------
# 6. Bond death direct-witness rule becomes a formal SocialBond invariant.
# -----------------------------------------------------------------------------
bond = 'src/Creatures/DesertBatfly/DesertBatflySocialBond.cs'
replace_once(bond,
'''internal static class DesertBatflySocialBond\n{\n''',
'''internal static class DesertBatflySocialBond\n{\n    internal const float DirectDeathWitnessRadius = 340f;\n''')
replace_once(bond,
'''    internal static void OnBondPartnerDeath(DesertBatfly observer, DesertBatfly victim, Creature killer)\n    {\n        if (!Available(observer) || !observer.Consious || victim == null || !victim.dead || observer.room != victim.room) return;\n''',
'''    internal static bool IsDirectDeathWitness(\n        DesertBatfly observer,\n        DesertBatfly victim,\n        Creature killer)\n    {\n        if (!Available(observer) || !observer.Consious || victim == null || observer == victim ||\n            observer.room == null || victim.room != observer.room)\n            return false;\n\n        float distance = Vector2.Distance(observer.mainBodyChunk.pos, victim.mainBodyChunk.pos);\n        if (distance <= DirectDeathWitnessRadius &&\n            (observer.room.VisualContact(observer.mainBodyChunk.pos, victim.mainBodyChunk.pos) ||\n             (killer?.mainBodyChunk != null && killer.room == observer.room &&\n              observer.room.VisualContact(observer.mainBodyChunk.pos, killer.mainBodyChunk.pos))))\n            return true;\n\n        return SamePhysicalChain(observer, victim);\n    }\n\n    internal static void OnBondPartnerDeath(DesertBatfly observer, DesertBatfly victim, Creature killer)\n    {\n        if (victim == null || !victim.dead || !IsDirectDeathWitness(observer, victim, killer)) return;\n''')
replace_once(bond,
'''        observer.DesertAI.BeginGriefResponse();\n    }\n}\n''',
'''        observer.DesertAI.BeginGriefResponse();\n    }\n\n    private static bool SamePhysicalChain(Fly a, Fly b)\n    {\n        if (a == null || b == null || a.room == null || a.room != b.room) return false;\n\n        Fly member = a.FirstInChain();\n        int guard = 0;\n        while (member != null && guard++ < 32)\n        {\n            if (member == b) return true;\n            member = member.NextInChain();\n        }\n\n        member = b.FirstInChain();\n        guard = 0;\n        while (member != null && guard++ < 32)\n        {\n            if (member == a) return true;\n            member = member.NextInChain();\n        }\n        return false;\n    }\n}\n''')


# -----------------------------------------------------------------------------
# 7. Intimidation directly separates direct fear from bounded Task12 propagation.
# -----------------------------------------------------------------------------
intim = 'src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs'
replace_once(intim,
'''    internal static bool IsExtremeVengeanceActive(DesertBatfly bat)\n    {\n        return bat != null && states.TryGetValue(bat, out State state) &&\n               state.Active && state.Vengeance != VengeanceMode.None;\n    }\n''',
'''    internal static bool IsExtremeVengeanceActive(DesertBatfly bat)\n    {\n        return bat != null && states.TryGetValue(bat, out State state) &&\n               state.Active && state.Vengeance != VengeanceMode.None;\n    }\n\n    internal static bool IsVengeanceAvenger(DesertBatfly bat)\n    {\n        return bat != null && states.TryGetValue(bat, out State state) && state.Active &&\n               state.Vengeance != VengeanceMode.None &&\n               state.Role == VengeanceParticipation.Avenger;\n    }\n''')
replace_once(intim,
'''    private static void ReceiveFear(\n        DesertBatfly bat,\n        Creature threat,\n        Vector2 eventPosition,\n        int tier,\n        float threatScale,\n        EventKind kind)\n    {\n        State state = StateFor(bat);\n''',
'''    private static void ReceiveFear(\n        DesertBatfly bat,\n        Creature threat,\n        Vector2 eventPosition,\n        int tier,\n        float threatScale,\n        EventKind kind)\n    {\n        if (tier != 0)\n        {\n            TraceIndirectFearSuppressed(bat, tier);\n            return;\n        }\n\n        State state = StateFor(bat);\n''')
replace_once(intim,
'''        bat.DesertAI.Threatened(threat, false);\n    }\n\n    private static void ReceiveCorpseReminder''',
'''        bat.DesertAI.ThreatenedAt(threat, eventPosition, false, false);\n        DesertBatflySignalRuntime.EmitAlarm(\n            bat,\n            threat,\n            eventPosition,\n            Custom.DirVec(bat.mainBodyChunk.pos, eventPosition),\n            Mathf.Clamp01(0.62f + Mathf.Clamp(threatScale, 0f, 1.5f) * 0.20f),\n            "direct Intimidation witness emits sole indirect Task12 Alarm generation");\n    }\n\n    private static void TraceIndirectFearSuppressed(DesertBatfly bat, int tier)\n    {\n        if (bat?.abstractCreature == null ||\n            !DryCycle.Debugging.AI.AIDebugTrace.IsWatched(bat.abstractCreature))\n            return;\n        DryCycle.Debugging.AI.AIDebugTrace.Record(\n            bat.abstractCreature,\n            DryCycle.Debugging.AI.AIDebugEventCategory.Social,\n            "Task12LegacyIndirectFearSuppressed",\n            $"tier={tier}",\n            "Secondary/Chain fear no longer applies Trauma/Fear directly; Task12 Alarm perception owns indirect propagation");\n    }\n\n    private static void ReceiveCorpseReminder''')
replace_once(intim,
'''        if (state.Vengeance != VengeanceMode.None && state.VengeanceTarget == threat)\n        {\n            state.Rage = Mathf.Max(state.Rage, rage);\n            if (state.Role == VengeanceParticipation.Avenger)\n                state.PassesRemaining = Mathf.Max(\n                    state.PassesRemaining,\n                    drive > 0.70f ? 2 : 1);\n            return;\n        }\n''',
'''        if (state.Vengeance != VengeanceMode.None && state.VengeanceTarget == threat)\n        {\n            state.Rage = Mathf.Max(state.Rage, rage);\n            if (state.Role == VengeanceParticipation.Avenger)\n            {\n                state.PassesRemaining = Mathf.Max(\n                    state.PassesRemaining,\n                    drive > 0.70f ? 2 : 1);\n                DesertBatflySignalRuntime.EmitRally(\n                    bat, threat, drive, "existing Avenger refreshes RallySignal");\n            }\n            return;\n        }\n''')
replace_once(intim,
'''        state.VengeanceTimer = Mathf.RoundToInt(\n            Mathf.Lerp(maxDelay, minDelay, drive)) + socialDelay;\n    }\n\n    private static void UpdateVengeance''',
'''        state.VengeanceTimer = Mathf.RoundToInt(\n            Mathf.Lerp(maxDelay, minDelay, drive)) + socialDelay;\n\n        // Deliver synchronously before ArmVengeanceGroup scores followers so Task12 Rally\n        // interest can participate in SocialBond.Motivation without a detour around this method.\n        if (state.Role == VengeanceParticipation.Avenger && !supportOnly && leader == null)\n            DesertBatflySignalRuntime.EmitRally(\n                bat, threat, drive, "new Avenger armed -> immediate Task12 RallySignal");\n    }\n\n    private static void UpdateVengeance''')


# -----------------------------------------------------------------------------
# 8. Task11 acute events call exact-position escape + one Task12 root explicitly.
# -----------------------------------------------------------------------------
threatrt = 'src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatRuntime.cs'
replace_once(threatrt,
'''            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.Threatened(player, false);\n\n            if (player != null && evidence.Any &&\n''',
'''            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.ThreatenedAt(player, explosion.pos, false, false);\n\n            if (player != null && evidence.Any &&\n''')
replace_once(threatrt,
'''                AddEvidence(bat, player, evidence, witness, "witnessed explosion", true);\n            }\n        }\n    }\n\n    private static void BroadcastStartle''',
'''                AddEvidence(bat, player, evidence, witness, "witnessed explosion", true);\n            }\n        }\n\n        float alarmIntensity = Mathf.Clamp01(\n            0.60f + Mathf.Clamp01(explosion.damage) * 0.16f +\n            Mathf.InverseLerp(80f, 360f, explosion.rad) * 0.16f);\n        DesertBatflySignalRuntime.EmitAcuteAlarm(\n            room,\n            explosion.killTagHolder,\n            explosion.pos,\n            Mathf.Max(0.62f, alarmIntensity),\n            "Task11 acute explosion -> Task12 AlarmFlutter at real explosion center");\n    }\n\n    private static void BroadcastStartle''')
replace_once(threatrt,
'''            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.Threatened(player, false);\n\n            if (player != null && DB_VisibilityPolicy.CanObserve(\n''',
'''            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.ThreatenedAt(player, position, false, false);\n\n            if (player != null && DB_VisibilityPolicy.CanObserve(\n''')
replace_once(threatrt,
'''                AddEvidence(bat, player, evidence, multiplier, "firecracker startle", true);\n            }\n        }\n    }\n\n    private static void BroadcastMassCasualty''',
'''                AddEvidence(bat, player, evidence, multiplier, "firecracker startle", true);\n            }\n        }\n\n        DesertBatflySignalRuntime.EmitAcuteAlarm(\n            room, player, position, 0.82f,\n            "Task11 firecracker/startle -> Task12 AlarmFlutter at real startle center");\n    }\n\n    private static void BroadcastMassCasualty''')
replace_once(threatrt,
'''            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.Threatened(player, false);\n        }\n    }\n\n    private static void BroadcastWitnessEvidence''',
'''            if (!DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))\n                bat.DesertAI.ThreatenedAt(player, position, false, false);\n        }\n\n        DesertBatflySignalRuntime.EmitAcuteAlarm(\n            room, player, position, 0.96f,\n            "Task11 mass casualty -> high urgency Task12 AlarmFlutter");\n    }\n\n    private static void BroadcastWitnessEvidence''')


# -----------------------------------------------------------------------------
# 9. Task12 regression protects behavior and deleted implementation debt.
# -----------------------------------------------------------------------------
t12 = 'tests/DesertBatfly/Program.Task12.cs'
replace_once(t12,
'''        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);\n        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRoomRuntime", true);\n        Type integration = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", true);\n        Type vengeanceBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalVengeanceBridge", true);\n        Type threatBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalThreatBridge", true);\n        Type acuteBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalAcuteBridge", true);\n        Type directWitnessBridge = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalDirectWitnessBridge", true);\n        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);\n        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);\n        Type graphics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyGraphics", true);\n''',
'''        Type runtime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);\n        Type roomRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRoomRuntime", true);\n        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialLife", true);\n        Type socialBond = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialBond", true);\n        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);\n        Type graphics = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyGraphics", true);\n        Type ai = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);\n        Type combat = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_CombatRuntime", true);\n        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);\n        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);\n        Type eventConsumers = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_EventConsumers", true);\n''')
old_bridge_tests = '''        Check(integration.GetMethod("ApplyAlarm", Flags) != null &&\n              integration.GetMethod("FindSocialHarassTargetHook", Flags) != null &&\n              integration.GetMethod("FindRoostSourceHook", Flags) != null &&\n              integration.GetMethod("HandleIndirectFear", Flags) != null,\n            "Task12 integration replaces legacy alarm/indirect fear and routes Harass/Roost through signals");\n\n        MethodInfo vengeanceEnable = vengeanceBridge.GetMethod("Enable", Flags);\n        MethodInfo vengeanceDisable = vengeanceBridge.GetMethod("Disable", Flags);\n        Check(MethodCallsTask12(vengeanceEnable, threatBridge, "Enable") &&\n              MethodCallsTask12(vengeanceDisable, threatBridge, "Disable") &&\n              MethodCallsTask12(vengeanceEnable, acuteBridge, "Enable") &&\n              MethodCallsTask12(vengeanceDisable, acuteBridge, "Disable") &&\n              MethodCallsTask12(vengeanceEnable, directWitnessBridge, "Enable") &&\n              MethodCallsTask12(vengeanceDisable, directWitnessBridge, "Disable"),\n            "Task12 Task11-response, acute-event and direct-witness bridges share the signal lifecycle");\n\n        Check(acuteBridge.GetMethod("ExplosionHook", Flags) != null &&\n              acuteBridge.GetMethod("StartleHook", Flags) != null &&\n              acuteBridge.GetMethod("MassCasualtyHook", Flags) != null &&\n              acuteBridge.GetMethod("EmitAcuteAlarm", Flags) != null,\n            "Task12 acute bridge converts real Task11 explosion/startle/casualty positions into Alarm roots");\n        Check(directWitnessBridge.GetMethod("IsDirectWitness", Flags) != null,\n            "Task12 has an explicit direct-witness gate for persistent Bond-death Grief/Trauma");\n\n        Check(!TypeCallsTask12Forbidden(threatBridge) && !TypeCallsTask12Forbidden(acuteBridge) &&\n              !TypeCallsTask12Forbidden(directWitnessBridge) && !TypeCallsTask12Forbidden(runtime) &&\n              !TypeCallsTask12Forbidden(integration),\n            "Task12 signal layer never reads input, writes ThreatSignature evidence or directly owns BodyChunk velocity");\n'''
new_bridge_tests = '''        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalVengeanceBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalThreatBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalAcuteBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalDirectWitnessBridge", false) == null,\n            "Task12 R5 retires all internal Reflection/RuntimeDetour signal integration layers");\n\n        Check(runtime.GetMethod("ApplyAlarm", Flags) != null &&\n              runtime.GetMethod("EmitAcuteAlarm", Flags) != null &&\n              runtime.GetMethod("EmitRally", Flags) != null &&\n              ai.GetMethod("ThreatenedAt", Flags) != null &&\n              socialBond.GetMethod("IsDirectDeathWitness", Flags) != null,\n            "Task12 direct APIs own anonymous alarm escape, acute roots, Rally and grief witness boundaries");\n        Check(combat.GetMethod("FindSocialHarassTarget", Flags) != null &&\n              social.GetMethod("FindRoostSource", Flags) != null,\n            "Task12 Harass/Roost influence is consumed directly by Combat and Social owners");\n\n        MethodInfo reportExplosion = threatRuntime.GetMethod("ReportExplosion", Flags);\n        MethodInfo startle = threatRuntime.GetMethod("BroadcastStartle", Flags);\n        MethodInfo mass = threatRuntime.GetMethod("BroadcastMassCasualty", Flags);\n        MethodInfo receiveFear = intimidation.GetMethod("ReceiveFear", Flags);\n        MethodInfo armVengeance = intimidation.GetMethod("ArmVengeance", Flags);\n        MethodInfo captureConsumer = eventConsumers.GetMethod("OnCapture", Flags);\n        Check(MethodCallOffset(reportExplosion, runtime, "EmitAcuteAlarm") >= 0 &&\n              MethodCallOffset(startle, runtime, "EmitAcuteAlarm") >= 0 &&\n              MethodCallOffset(mass, runtime, "EmitAcuteAlarm") >= 0,\n            "Task11 acute events explicitly emit one Task12 root at the real event position");\n        Check(MethodCallOffset(receiveFear, runtime, "EmitAlarm") >= 0 &&\n              MethodCallOffset(armVengeance, runtime, "EmitRally") >= 0 &&\n              MethodCallOffset(captureConsumer, runtime, "EmitDistress") >= 0,\n            "fear, Vengeance and capture semantics publish through direct Task12 APIs");\n\n        Check(!TypeCallsTask12Forbidden(runtime) && !TypeCallsTask12Forbidden(socialBond),\n            "Task12 signal data path never reads input, writes ThreatSignature evidence or directly owns BodyChunk velocity");\n'''
replace_once(t12, old_bridge_tests, new_bridge_tests)
replace_once(t12,
'''        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);\n        Type threatRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyThreatRuntime", true);\n''',
'''        MethodInfo updateAI = hooks.GetMethod("UpdateAI", Flags);\n''')
replace_once(t12,
'''        Type intimidation = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);\n        Check(intimidation.GetNestedType("SocialRole", Flags) == null &&\n''',
'''        Check(intimidation.GetNestedType("SocialRole", Flags) == null &&\n''')
replace_once(t12,
'''            "Task 12 signals: six-kind model, bounded room/generation state, relay cap, indirect-fear migration, accurate acute roots, direct-witness grief boundary, Task11 read-only boundary, lifecycle, pipeline, graphics, vengeance terminology and anti-role guards verified.");\n''',
'''            "Task 12 signals: six-kind model, bounded room/generation state, relay cap, direct API indirect-fear migration, accurate acute roots, direct-witness grief boundary, Task11 read-only boundary, pipeline, graphics, vengeance terminology and anti-role guards verified.");\n''')


# -----------------------------------------------------------------------------
# 10. R5 regression + status.
# -----------------------------------------------------------------------------
r5 = 'tests/DesertBatfly/Program.Task14R5.cs'
replace_once(r5,
'''        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", false) == null,\n            "R5 B3 physically removes Task09 and Survival RuntimeDetour bridges");\n''',
'''        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalTask09Bridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEnvironmentalSurvivalBridge", false) == null,\n            "R5 B3 physically removes Task09 and Survival RuntimeDetour bridges");\n        Check(mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalIntegration", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalVengeanceBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalThreatBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalAcuteBridge", false) == null &&\n              mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalDirectWitnessBridge", false) == null,\n            "R5 B4 physically removes the Task12 internal detour hub and four signal bridges");\n''')
replace_once(r5,
'''        Console.WriteLine("Task14 R5 B3: Task09 and same-room survival consume explicit Environment policy/behavior; both RuntimeDetour bridges are removed.");\n''',
'''        Type signalRuntime = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySignalRuntime", true);\n        Check(signalRuntime.GetMethod("EmitAcuteAlarm", Flags) != null &&\n              signalRuntime.GetMethod("EmitRally", Flags) != null,\n            "R5 B4 replaces signal detours with direct domain APIs");\n\n        Console.WriteLine("Task14 R5 B4: Environment and Signal internal detours are retired; direct domain APIs preserve cross-domain behavior.");\n''')

status = 'docs/Discussion/Task_14_R5_BridgeDebtStatus.txt'
text = read(status)
text = text.replace('Revision: R5-B3 / 2026-09-07', 'Revision: R5-B4 / 2026-09-07', 1)
text = text.replace('Status: 【R5 进行中 / B3 Task09 + same-room survival bridges removed】',
                    'Status: 【R5 进行中 / B4 Signal detour family removed】', 1)
text += '''\n\n======================================================================\n7. R5-B4 closed\n======================================================================\n\n- DesertBatflySignalIntegration.cs DELETED.\n- DesertBatflySignalAcuteBridge.cs DELETED.\n- DesertBatflySignalDirectWitnessBridge.cs DELETED.\n- DesertBatflySignalThreatBridge.cs DELETED.\n- DesertBatflySignalVengeanceBridge.cs DELETED.\n- DesertBatflyAI now exposes exact-origin ThreatenedAt and emits AlarmFlutter directly;\n  signal-received escape uses emitAlarm=false, so relay ownership stays in SignalRoomRuntime.\n- Task11 Explosion/Startle/MassCasualty explicitly publish one acute Alarm root at the real\n  event position after updating directly affected bats.\n- SignalRuntime reads only the receiver's own Task11 memory to modulate response strength.\n- DB_CombatRuntime consumes HarassSignal target interest directly; Task10 Social consumes\n  RoostCall source interest directly. No private-AI spying detour remains.\n- SocialBond owns the direct-death-witness invariant for persistent Grief/Trauma.\n- Intimidation suppresses legacy tier-1+/chain persistent fear directly and publishes the\n  bounded Alarm from a direct witness; Vengeance leader arming publishes Rally synchronously\n  before follower scoring.\n- DB_EventConsumers publishes capture Distress/Alarm directly from the canonical Capture event.\n\nRemaining R5 work is DesertBatflyRuntimePatch classification plus a final repository-wide\nBridge/Reflection/RuntimeDetour audit before R5 closure.\n'''
write(status, text)


# -----------------------------------------------------------------------------
# 11. Physical deletion + self-cleanup.
# -----------------------------------------------------------------------------
for doomed in [
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalIntegration.cs',
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalAcuteBridge.cs',
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalDirectWitnessBridge.cs',
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalThreatBridge.cs',
    'src/Creatures/DesertBatfly/Signals/DesertBatflySignalVengeanceBridge.cs',
    'scripts/task14_r5_b4_apply.py',
    '.github/workflows/task14-r5-b4.yml',
]:
    p = ROOT / doomed
    if p.exists():
        p.unlink()
