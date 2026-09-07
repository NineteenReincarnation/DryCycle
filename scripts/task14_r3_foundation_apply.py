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
# Travel: add a side-effect-free ownership preflight. Suspended history is NOT a veto.
# -----------------------------------------------------------------------------
travel_path = "src/Creatures/DesertBatfly/DesertBatflyTravelNavigation.cs"
travel = read(travel_path)
travel = replace_once(
    travel,
    """    internal static bool HasIntent(AbstractCreature creature) =>
        creature != null && intents.ContainsKey(Key(creature.ID));

    internal static bool TryGetDebugState(AbstractCreature creature, out DesertBatflyTravelDebugState state)
""",
    """    internal static bool HasIntent(AbstractCreature creature) =>
        creature != null && intents.ContainsKey(Key(creature.ID));

    /// <summary>
    /// R3 proposal query. This method never decrements departure delay, replans, calls
    /// LeaveRoom or mutates TravelIntent. Historical Suspended state is deliberately not a
    /// veto: once the current blocker clears, the arbiter may select Travel and the existing
    /// executor is allowed to resume/replan normally.
    /// </summary>
    internal static bool CanOwnRealizedFrame(DesertBatfly bat, out string reason)
    {
        reason = "no active realized travel intent";
        if (bat?.abstractCreature == null || bat.AI == null || bat.room == null ||
            bat.dead || !bat.Consious || bat.inShortcut)
            return false;
        if (!intents.TryGetValue(Key(bat.abstractCreature.ID), out TravelIntent intent))
            return false;

        if (intent.Purpose == DesertBatflyTravelPurpose.EmergencyRefuge && intent.WaitingAtRefuge)
        {
            reason = "EmergencyRefuge hold remains Travel-owned";
            return true;
        }
        if (intent.DepartureDelay > 0)
        {
            reason = "scheduled / staggered departure remains Travel-owned";
            return true;
        }
        if (RestrainedByNonFly(bat))
        {
            reason = "Travel yielded: restrained by non-Fly";
            return false;
        }
        if (bat.DesertAI.HasImmediateDanger)
        {
            reason = "Travel yielded: immediate threat / escape";
            return false;
        }
        if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering)
        {
            reason = "Travel yielded: severe injury / recovery";
            return false;
        }
        if (!intent.Route.Valid)
        {
            reason = "Travel yielded: route currently invalid";
            return false;
        }

        reason = intent.Suspended
            ? "Travel eligible to resume after higher-priority suspension"
            : string.IsNullOrEmpty(intent.StatusReason) ? "active realized Travel intent" : intent.StatusReason;
        return true;
    }

    internal static bool TryGetDebugState(AbstractCreature creature, out DesertBatflyTravelDebugState state)
""",
    "Travel ownership preflight")
write(travel_path, travel)


# -----------------------------------------------------------------------------
# Intimidation: expose read-only Vengeance target; no sibling private-state reflection.
# -----------------------------------------------------------------------------
intimidation_path = "src/Creatures/DesertBatfly/DesertBatflyIntimidation.cs"
intimidation = read(intimidation_path)
intimidation = replace_once(
    intimidation,
    """    internal static bool IsExtremeVengeanceActive(DesertBatfly bat)
    {
        return bat != null && states.TryGetValue(bat, out State state) &&
               state.Active && state.Vengeance != VengeanceMode.None;
    }

    // Read-only: fear checks must never create a morale state, especially for corpses.
""",
    """    internal static bool IsExtremeVengeanceActive(DesertBatfly bat)
    {
        return bat != null && states.TryGetValue(bat, out State state) &&
               state.Active && state.Vengeance != VengeanceMode.None;
    }

    /// <summary>
    /// Read-only R3 query for current Vengeance facts. It never creates State and never
    /// changes Vengeance commitment; consumers no longer reflect into this runtime.
    /// </summary>
    internal static bool TryGetVengeanceTarget(DesertBatfly bat, out Creature target)
    {
        target = null;
        if (bat == null || !states.TryGetValue(bat, out State state) || !state.Active ||
            state.Vengeance == VengeanceMode.None || state.VengeanceTarget == null)
            return false;
        target = state.VengeanceTarget;
        return true;
    }

    // Read-only: fear checks must never create a morale state, especially for corpses.
""",
    "Intimidation explicit Vengeance target API")
write(intimidation_path, intimidation)


# -----------------------------------------------------------------------------
# Hooks: lifecycle + central Travel arbitration. Lower domains remain open R3 migration.
# -----------------------------------------------------------------------------
hooks_path = "src/Creatures/DesertBatfly/DesertBatflyHooks.cs"
hooks = read(hooks_path)
hooks = replace_once(
    hooks,
    """        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DesertBatflyIntimidation.Reset();
""",
    """        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DB_FrameContextRuntime.Reset();
        DB_BehaviorArbiter.Reset();
        DesertBatflyIntimidation.Reset();
""",
    "R3 lifecycle enable reset")
hooks = replace_once(
    hooks,
    """        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DesertBatflyIntimidation.Reset();
        DesertBatflyWarpCompatibility.Disable();
""",
    """        DB_CorpseWarningRuntime.Reset();
        DB_RoomContext.Reset();
        DB_FrameContextRuntime.Reset();
        DB_BehaviorArbiter.Reset();
        DesertBatflyIntimidation.Reset();
        DesertBatflyWarpCompatibility.Disable();
""",
    "R3 lifecycle disable reset")
hooks = replace_once(
    hooks,
    """            DesertBatflySignalRuntime.Forget(desert);
            DesertBatflyThreatRuntime.Forget(desert);
            DesertBatflyEnvironmentalBehavior.Forget(desert);
""",
    """            DesertBatflySignalRuntime.Forget(desert);
            DesertBatflyThreatRuntime.Forget(desert);
            DesertBatflyEnvironmentalBehavior.Forget(desert);
            DB_FrameContextRuntime.Forget(desert);
            DB_BehaviorArbiter.Forget(desert);
""",
    "R3 room transition cache forget")

hooks = replace_block(
    hooks,
    "    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)\n",
    "    private static bool RestrainedByNonFly(DesertBatfly fly)\n",
    """    private static void UpdateAI(On.FlyAI.orig_Update orig, FlyAI self)
    {
        if (self.fly is DesertBatfly suspended &&
            (suspended.Emergence.Active || RestrainedByNonFly(suspended)))
        {
            DB_BehaviorArbiter.ResolveFrame(suspended);
            DesertBatflySocialLife.CancelForPriority(suspended, "unavailable / restraint / emergence");
            suspended.DesertAI.Update();
            DesertBatflySocialLife.SampleTrace(suspended);
            DesertBatflyDebugTrace.Sample(suspended);
            return;
        }

        orig(self);
        if (self.fly is not DesertBatfly desert) return;
        DesertBatflyEnvironmentalIntegration.Register(desert);

        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)
        {
            if (DesertBatflyTravelNavigation.TryDriveRealized(desert))
            {
                DesertBatflySocialLife.CancelForPriority(desert, "R3 PrimaryOwner=Travel");
                desert.DesertAI.CancelAttack();
                if (AIDebugTrace.IsWatched(desert.abstractCreature))
                    AIDebugTrace.RecordChange(
                        desert.abstractCreature,
                        AIDebugEventCategory.Decision,
                        "PrimaryOwner",
                        ownership.PrimaryOwner,
                        ownership.Reason);
                DesertBatflySocialLife.SampleTrace(desert);
                DesertBatflyDebugTrace.Sample(desert);
                return;
            }

            // Route validation/replanning can still refuse after a side-effect-free preflight.
            // Re-resolve this exact frame with Travel explicitly rejected so Observatory and
            // later Vengeance priority never report a movement owner that did not execute.
            ownership = DB_BehaviorArbiter.ResolveFrame(
                desert,
                DB_BehaviorOwner.Travel,
                "Travel executor yielded after preflight / route validation");
        }

        // R3 foundation currently centralizes ownership classification and Travel execution.
        // Injury/Vengeance/Environment/Social are still being migrated behind proposals;
        // keep their existing execution order until each domain has a dedicated executor.
        desert.DesertAI.Update();
        DesertBatflyThreatRuntime.Update(desert);
        DesertBatflyThreatTactics.TryApplyOrdinaryProjectileEvade(desert);
        DesertBatflyThreatTrace.Sample(desert);
        DesertBatflySignalRuntime.Update(desert);
        DesertBatflyEnvironmentalBehavior.Update(desert);
        DesertBatflySocialLife.Update(desert);
        DesertBatflySocialLife.SampleTrace(desert);
        DesertBatflyDebugTrace.Sample(desert);
    }

""",
    "R3 central UpdateAI arbitration")

hooks = replace_block(
    hooks,
    "    private static void Rain(On.FlyAI.orig_FleeFromRainUpdate orig, FlyAI self)\n",
    "    private static void Follow(On.FlyAI.orig_UpdateFollowDijsktra orig, FlyAI self)\n",
    """    private static void Rain(On.FlyAI.orig_FleeFromRainUpdate orig, FlyAI self)
    {
        if (self.fly is not DesertBatfly desert)
        {
            orig(self);
            return;
        }

        // Do not execute Travel from this nested vanilla callback. If Travel wins, suppress
        // native rain steering here and let the enclosing UpdateAI execute Travel exactly once.
        DB_BehaviorResolution ownership = DB_BehaviorArbiter.ResolveFrame(desert);
        if (ownership.PrimaryOwner == DB_BehaviorOwner.Travel)
        {
            DesertBatflySocialLife.CancelForPriority(desert, "R3 Travel owns enclosing AI frame");
            return;
        }

        DesertBatflySocialLife.CancelForPriority(desert, "rain priority");
        if (self.room.hives.Length > 0)
        {
            orig(self);
            return;
        }

        self.afraid = Mathf.Max(self.afraid, 2f);
    }

""",
    "R3 rain nested Travel arbitration")

if "MarkTravelOwnedFrame" in hooks:
    raise RuntimeError("legacy parallel Travel frame stamp remains in hooks")
write(hooks_path, hooks)


# -----------------------------------------------------------------------------
# Observatory: replace guessed ControlOwner with actual arbiter resolution.
# -----------------------------------------------------------------------------
debug_path = "src/Debug/AIDebugger/Sources/DesertBatflyDebugSource.cs"
debug = read(debug_path)
debug = replace_block(
    debug,
    "    private static string ControlOwner(DesertBatfly bat)\n",
    "    private static T Read<T>(FieldInfo field, object instance)\n",
    """    private static string ControlOwner(DesertBatfly bat)
    {
        if (bat == null) return "R3 / unresolved";
        if (!DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution resolution))
            return "R3 / unresolved";
        int clock = bat.room?.game?.clock ?? int.MinValue;
        if (resolution.Clock != clock)
            return "R3 / unresolved";
        return $"{resolution.PrimaryOwner} / {resolution.WinningProposal.BehaviorKind}";
    }

""",
    "Observatory actual PrimaryOwner")
write(debug_path, debug)


# Structural guards before committing.
assert "CanOwnRealizedFrame" in read(travel_path)
assert "TryGetVengeanceTarget" in read(intimidation_path)
assert "DB_BehaviorArbiter.ResolveFrame" in read(hooks_path)
assert "MarkTravelOwnedFrame" not in read(hooks_path)
assert "DB_BehaviorArbiter.TryGetResolution" in read(debug_path)
assert "statesField" not in read("src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatVengeanceBridge.cs")
assert "travelFrames" not in read("src/Creatures/DesertBatfly/ThreatSignature/DesertBatflyThreatVengeanceBridge.cs")

# Self-remove one-shot machinery from final tree.
for transient in [
    "scripts/task14_r3_foundation_apply.py",
    ".github/workflows/task14-r3-foundation-one-shot.yml",
]:
    p = ROOT / transient
    if p.exists():
        p.unlink()

print("Task14 R3 foundation wiring completed successfully.")
