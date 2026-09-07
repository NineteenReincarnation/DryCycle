from pathlib import Path

ROOT = Path('.')

def read(path):
    return (ROOT / path).read_text(encoding='utf-8')

def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8')

def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 match, found {count}')
    return text.replace(old, new, 1)

def method_span(text, anchor):
    start = text.find(anchor)
    if start < 0:
        raise SystemExit(f'method anchor not found: {anchor}')
    line_start = text.rfind('\n', 0, start) + 1
    brace = text.find('{', start)
    if brace < 0:
        raise SystemExit(f'opening brace not found: {anchor}')
    i = brace
    depth = 0
    state = 'code'
    while i < len(text):
        c = text[i]
        n = text[i+1] if i+1 < len(text) else ''
        if state == 'code':
            if c == '/' and n == '/': state='line'; i += 2; continue
            if c == '/' and n == '*': state='block'; i += 2; continue
            if c == '"': state='string'; i += 1; continue
            if c == "'": state='char'; i += 1; continue
            if c == '{': depth += 1
            elif c == '}':
                depth -= 1
                if depth == 0: return line_start, i+1
            i += 1; continue
        if state == 'line':
            if c == '\n': state='code'
            i += 1; continue
        if state == 'block':
            if c == '*' and n == '/': state='code'; i += 2; continue
            i += 1; continue
        if state == 'string':
            if c == '\\': i += 2; continue
            if c == '"': state='code'
            i += 1; continue
        if state == 'char':
            if c == '\\': i += 2; continue
            if c == "'": state='code'
            i += 1; continue
    raise SystemExit(f'unclosed method: {anchor}')

def replace_method(path, anchor, replacement):
    text = read(path)
    start, end = method_span(text, anchor)
    write(path, text[:start] + replacement.rstrip() + text[end:])

def remove_method(path, anchor):
    text = read(path)
    start, end = method_span(text, anchor)
    while end < len(text) and text[end] == '\n': end += 1
    write(path, text[:start] + text[end:])

# -----------------------------------------------------------------------------
# 1. Expand DB_CombatRuntime into the owner of target/motivation/retaliation state.
# -----------------------------------------------------------------------------
path = 'src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs'
text = read(path)
text = replace_once(
    text,
    '    private readonly DesertBatflyAI ai;\n    private readonly DesertBatfly fly;\n\n    private int ticks;',
    '''    private readonly DesertBatflyAI ai;\n    private readonly DesertBatfly fly;\n\n    private Creature target;\n    private Creature attacker;\n    private int memory;\n    private int retaliationCharges;\n    private int retaliationRecovery;\n    private Creature scanCandidate;\n    private Player rememberedCandidate;\n    private float closestCandidate = float.MaxValue;\n\n    private int ticks;''',
    'combat selection fields')
text = replace_once(
    text,
    '    internal bool HasSlot => hasSlot;\n',
    '''    internal enum SelectionResult\n    {\n        Ready,\n        NoTarget,\n        Cooldown\n    }\n\n    internal Creature Target => target;\n    internal Creature Attacker => attacker;\n    internal int Memory => memory;\n    internal int RetaliationCharges => retaliationCharges;\n    internal int RetaliationRecovery => retaliationRecovery;\n    internal bool HasSlot => hasSlot;\n''',
    'combat selection properties')
text = replace_once(
    text,
    '        ticks = 0;\n        unseen = 0;',
    '''        target = null;\n        attacker = null;\n        memory = 0;\n        retaliationCharges = 0;\n        retaliationRecovery = 0;\n        scanCandidate = null;\n        rememberedCandidate = null;\n        closestCandidate = float.MaxValue;\n        ticks = 0;\n        unseen = 0;''',
    'combat reset selection state')
marker = '    internal bool TryExecuteOwned()\n'
selection_methods = r'''    internal void TickMemory()
    {
        if (memory > 0 && --memory == 0) attacker = null;
        if (retaliationRecovery > 0) retaliationRecovery--;
    }

    internal void RecordAttacker(Creature source, float retaliationStrength = 0f)
    {
        if (source == null || source == fly || source is DesertBatfly) return;
        attacker = source;
        memory = Mathf.Max(memory, DesertBatflyTuning.AttackerMemory);
        if (retaliationStrength > 0f && source is Player player)
            ArmRetaliation(player, retaliationStrength);
    }

    internal void RecordGrabber(Player player)
    {
        if (player == null) return;
        attacker = player;
        memory = Mathf.Max(memory, DesertBatflyTuning.AttackerMemory);
    }

    internal void ArmRetaliation(Player player, float strength)
    {
        if (fly.Injury.BlocksCombat || !fly.Personality.Aggressive || player == null ||
            ai.IsTraumatizedPlayer(player))
            return;

        float drive = fly.Personality.AggressionDrive;
        float secondPassChance = Mathf.Clamp01((drive - 0.62f) / 0.38f) *
            Mathf.Lerp(0.25f, 0.65f, Mathf.Clamp01(strength));
        int passes = Random.value < secondPassChance ? 2 : 1;
        retaliationCharges = Mathf.Max(retaliationCharges, passes);
        retaliationRecovery = 0;
    }

    internal void ClearRetaliation()
    {
        retaliationCharges = 0;
        retaliationRecovery = 0;
    }

    internal void ClearMemoryAndRetaliation()
    {
        attacker = null;
        memory = 0;
        ClearRetaliation();
    }

    internal void SuppressHostility(Creature source)
    {
        if (source == null) return;
        if (target == source)
        {
            ClearAttackState();
            target = null;
            if (ai.Mode is DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
                DesertBatflyAI.Activity.Dive or DesertBatflyAI.Activity.Attach or
                DesertBatflyAI.Activity.RetaliationCharge or DesertBatflyAI.Activity.Interfere)
                ai.SetMode(DesertBatflyAI.Activity.Flight);
        }
        if (attacker == source)
        {
            attacker = null;
            memory = 0;
            ClearRetaliation();
        }
        ClearVisibilityTracking();
    }

    internal void SetTarget(Creature value) => target = value;

    internal void ClearTarget()
    {
        target = null;
        scanCandidate = null;
        rememberedCandidate = null;
    }

    internal void BeginCandidateScan()
    {
        scanCandidate = null;
        rememberedCandidate = null;
        closestCandidate = DesertBatflyTuning.SightRange;
    }

    internal void ConsiderCandidate(Creature creature, float distance)
    {
        if (!ai.Valid(creature)) return;
        if (creature is Player player && fly.Personality.Aggressive &&
            ai.IsRememberedPlayer(player) && !ai.IsTraumatizedPlayer(player))
            rememberedCandidate = player;

        if (distance < closestCandidate && CanHarass(creature))
        {
            closestCandidate = distance;
            scanCandidate = creature;
        }
    }

    internal void CompleteCandidateScan(bool retreatActive)
    {
        if (target != null || !fly.Personality.Aggressive || retreatActive) return;

        bool retaliationPending = retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && !retaliationPending) return;
        if (!GriefAllowsHarass()) return;

        Player socialCandidate = FindSocialHarassTarget();
        float observeThreshold = Mathf.Lerp(
            DesertBatflyTuning.ObserveThirst,
            0.18f,
            fly.Personality.AggressionDrive * 0.45f);
        float socialMotivationScale = socialCandidate != null
            ? Mathf.Lerp(1f, 0.72f, fly.Personality.Conformity)
            : 1f;
        bool motivated = fly.DesertState.Thirst > observeThreshold * socialMotivationScale ||
                         memory > 0 || rememberedCandidate != null;
        if (!motivated) return;

        if (ai.Valid(attacker) && CanHarass(attacker))
            target = attacker;
        else if (rememberedCandidate != null)
            target = rememberedCandidate;
        else if (socialCandidate != null)
            target = socialCandidate;
        else
            target = scanCandidate;

        if (target != null)
            ai.SetMode(DesertBatflyAI.Activity.Observe);
    }

    internal SelectionResult PrepareSelection()
    {
        bool retaliationReady = fly.Personality.Aggressive &&
            retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && ai.Mode != DesertBatflyAI.Activity.Attach &&
            ai.Mode != DesertBatflyAI.Activity.Interfere && !retaliationReady)
        {
            ClearAttackState();
            target = null;
            ai.SetMode(DesertBatflyAI.Activity.Cooldown);
            return SelectionResult.Cooldown;
        }

        if (!fly.Personality.Aggressive || !GriefAllowsHarass())
        {
            if (ai.Mode != DesertBatflyAI.Activity.Roost)
            {
                ClearAttackState();
                target = null;
                ai.SetMode(DesertBatflyAI.Activity.Flight);
            }
            return SelectionResult.NoTarget;
        }

        if (!ai.Valid(target))
        {
            ClearAttackState();
            target = null;
            ai.SetMode(DesertBatflyAI.Activity.Flight);
            if (memory > 0 && ai.Valid(attacker) && CanHarass(attacker))
                target = attacker;
            if (target == null)
                return SelectionResult.NoTarget;
            ai.SetMode(DesertBatflyAI.Activity.Observe);
        }

        if (ai.Mode is DesertBatflyAI.Activity.Flight or DesertBatflyAI.Activity.Cooldown or
            DesertBatflyAI.Activity.Roost)
            ai.SetMode(DesertBatflyAI.Activity.Observe);
        return SelectionResult.Ready;
    }

    private bool GriefAllowsHarass() => !fly.Injury.BlocksCombat &&
        (fly.DesertState.GriefStrength <= 0f ||
         fly.DesertState.Thirst * fly.DesertState.GriefAttackScale >= DesertBatflyTuning.ObserveThirst) &&
        (fly.Injury.AggressionScale >= 0.99f ||
         fly.DesertState.Thirst * fly.Injury.AggressionScale >= DesertBatflyTuning.ObserveThirst);

    private bool CanHarass(Creature creature)
    {
        if (creature == fly || creature is DesertBatfly || !ai.Valid(creature)) return false;
        if (!GriefAllowsHarass()) return false;
        if (creature is Player player) return !ai.IsTraumatizedPlayer(player);

        CreatureTemplate.Relationship relation = fly.Template.CreatureRelationship(creature.Template);
        CreatureTemplate.Relationship reverse = creature.Template.CreatureRelationship(fly.Template);
        return creature.TotalMass <= DesertBatflyTuning.LightTargetMass &&
               relation.type != CreatureTemplate.Relationship.Type.Afraid &&
               reverse.type != CreatureTemplate.Relationship.Type.Eats &&
               reverse.type != CreatureTemplate.Relationship.Type.Attacks;
    }

    private Player FindSocialHarassTarget()
    {
        if (fly.Personality.Conformity < 0.42f || fly.room == null) return null;
        float socialDrive = fly.Personality.Conformity * 0.55f +
                            fly.Personality.AggressionDrive * 0.25f +
                            fly.Personality.Nerve * 0.20f;
        if (socialDrive < 0.52f) return null;

        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var bats = context?.Bats;
        if (bats == null) return null;

        Player best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < bats.Count; i++)
        {
            DesertBatfly bat = bats[i];
            if (bat == null || bat == fly || !bat.Consious ||
                bat.DesertAI.Target is not Player otherTarget || !CanHarass(otherTarget))
                continue;
            if (bat.DesertAI.Mode is not (
                DesertBatflyAI.Activity.Observe or DesertBatflyAI.Activity.Approach or
                DesertBatflyAI.Activity.Circle or DesertBatflyAI.Activity.FakeDive or
                DesertBatflyAI.Activity.Dive))
                continue;

            float neighbourDistance = Vector2.Distance(fly.mainBodyChunk.pos, bat.mainBodyChunk.pos);
            if (neighbourDistance > 210f ||
                (neighbourDistance > 95f && !DB_VisibilityPolicy.CanObserve(
                    fly, bat.mainBodyChunk.pos, 210f, DB_VisibilityChannel.Social)))
                continue;

            float score = socialDrive * 1.2f - neighbourDistance / 420f +
                          bat.Personality.AggressionDrive * 0.18f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = otherTarget;
        }
        return best;
    }

'''
text = replace_once(text, marker, selection_methods + marker, 'insert combat selection methods')

# Internal combat execution uses its own target/memory/retaliation state directly.
text = text.replace('ai.Target', 'target')
text = text.replace('ai.CombatAttacker', 'attacker')
text = text.replace('ai.CombatMemory', 'memory')
text = text.replace('ai.CombatRetaliationCharges', 'retaliationCharges')
text = text.replace('ai.CombatRetaliationRecovery', 'retaliationRecovery')
text = text.replace('ai.ClearCombatTarget()', 'ClearTarget()')
write(path, text)

# -----------------------------------------------------------------------------
# 2. AI shell: delegate combat state and retain only danger/approach perception.
# -----------------------------------------------------------------------------
path = 'src/Creatures/DesertBatfly/DesertBatflyAI.cs'
text = read(path)
text = replace_once(
    text,
    '    internal Activity Mode { get; private set; }\n    internal Creature Target { get; private set; }\n\n    private Creature attacker;\n    private Creature danger;\n    private int memory, retreat, ticks, scan, pursuit;\n    private int retaliationCharges, retaliationRecovery, recoverySearchCooldown;\n',
    '''    internal Activity Mode { get; private set; }\n    internal Creature Target => combat.Target;\n\n    private Creature danger;\n    private int retreat, ticks, scan, pursuit;\n    private int recoverySearchCooldown;\n''',
    'AI combat storage removal')
text = replace_once(
    text,
    '    internal Creature CombatAttacker => attacker;\n    internal int CombatMemory => memory;\n    internal int CombatRetaliationCharges { get => retaliationCharges; set => retaliationCharges = Mathf.Max(0, value); }\n    internal int CombatRetaliationRecovery { get => retaliationRecovery; set => retaliationRecovery = Mathf.Max(0, value); }\n',
    '''    internal Creature CombatAttacker => combat.Attacker;\n    internal int CombatMemory => combat.Memory;\n    internal int CombatRetaliationCharges => combat.RetaliationCharges;\n    internal int CombatRetaliationRecovery => combat.RetaliationRecovery;\n''',
    'AI compatibility combat properties')
write(path, text)

replace_method(path, '    internal void TickMemory()', '''    internal void TickMemory()
    {
        combat.TickMemory();

        if (!fly.Consious || RestrainedByNonFly() || fly.inShortcut)
        {
            if (IsInFlyChain(fly))
                BreakHangChain(null, DesertBatflyTuning.RetreatTicks);
            else if (Mode == Activity.Roost)
                StopRoost(false);
            CancelAttack();
            return;
        }

        TickGrabMemory();
        if (retreat > 0) retreat--;
    }''')

# ResetRoom no longer owns attacker/memory/retaliation state.
text = read(path)
text = replace_once(
    text,
    '        attacker = danger = null;\n        memory = retreat = pursuit = 0;\n        retaliationCharges = retaliationRecovery = 0;\n',
    '        danger = null;\n        retreat = pursuit = 0;\n',
    'AI ResetRoom combat state removal')
write(path, text)

replace_method(path, '    internal void Threatened(Creature source, bool directAttack = false)', '''    internal void Threatened(Creature source, bool directAttack = false)
    {
        ClearRecoveryNavigation();
        fly.Injury.SetRecovery(InjuryRecoveryState.None, null, "immediate threat / escape");
        if (source != null && source != fly && source is not DesertBatfly)
        {
            combat.RecordAttacker(source, directAttack ? 1f : 0f);
            escapeFrom = source.mainBodyChunk.pos;
        }
        else
        {
            escapeFrom = fly.mainBodyChunk.pos - Vector2.up * 20f;
        }

        if (IsInFlyChain(fly))
            BreakHangChain(source, DesertBatflyTuning.RetreatTicks);
        else
        {
            retreat = DesertBatflyTuning.RetreatTicks;
            CancelAttack();
            SetMode(Activity.Escape);
        }
        RaiseLocalAlarm();
    }''')

replace_method(path, '    internal void PlayerGrabbed(Player player)', '''    internal void PlayerGrabbed(Player player)
    {
        if (player == null) return;
        RememberGrabber(player, DesertBatflyTuning.GrabMemoryGain);
        combat.RecordGrabber(player);
        escapeFrom = player.mainBodyChunk.pos;

        if (IsInFlyChain(fly))
            BreakHangChain(player, DesertBatflyTuning.RetreatTicks);
        else
        {
            retreat = Mathf.Max(retreat, DesertBatflyTuning.RetreatTicks);
            CancelAttack();
            SetMode(Activity.Escape);
        }
        RaiseLocalAlarm();
    }''')

replace_method(path, '    internal void PlayerReleased(Player player, float releaseSpeed)', '''    internal void PlayerReleased(Player player, float releaseSpeed)
    {
        if (player == null || fly.dead) return;

        bool thrown = releaseSpeed >= DesertBatflyTuning.GrabThrowSpeed;
        if (thrown)
            RememberGrabber(player, DesertBatflyTuning.GrabThrowBonus);
        combat.RecordGrabber(player);
        escapeFrom = player.mainBodyChunk.pos;

        float trauma = PlayerTraumaStrength(player);
        bool traumaBlocksAggression = trauma >= DesertBatflyTuning.TraumaAggressionBlock;
        if (fly.Personality.Aggressive && !traumaBlocksAggression)
        {
            combat.ArmRetaliation(
                player,
                fly.DesertState.GrabMemoryStrength + (thrown ? 0.25f : 0f));
            retreat = Mathf.Clamp(retreat, 35, 60);
        }
        else
        {
            float fear = Mathf.Max(fly.DesertState.GrabMemoryStrength, trauma) *
                         Mathf.Lerp(1.15f, 0.8f, fly.Personality.Nerve);
            retreat = Mathf.Max(
                retreat,
                Mathf.RoundToInt(Mathf.Lerp(90f, 210f, Mathf.Clamp01(fear))));
            combat.ClearRetaliation();
            if (traumaBlocksAggression)
                combat.SuppressHostility(player);
        }

        CancelAttack();
        SetMode(Activity.Escape);
    }''')

remove_method(path, '    private void ArmRetaliation(Player player, float strength)')

replace_method(path, '    internal void CancelPhysicalAttack()', '''    internal void CancelPhysicalAttack()
    {
        if (Target != null || combat.HasSlot) CancelAttack();
        combat.ClearRetaliation();
    }''')
replace_method(path, '    internal void CancelAttack()', '''    internal void CancelAttack()
    {
        combat.ClearAttackState();
        combat.ClearTarget();
        SetMode(Activity.Flight);
    }''')
replace_method(path, '    internal void BeginGriefResponse()', '''    internal void BeginGriefResponse()
    {
        CancelAttack();
        combat.ClearMemoryAndRetaliation();
    }''')
replace_method(path, '    internal void SuppressHostility(Creature source)', '''    internal void SuppressHostility(Creature source)
    {
        if (source == null) return;
        combat.SuppressHostility(source);
        pursuit = 0;
    }''')

text = read(path)
text = replace_once(text, '    internal void ClearCombatTarget() => Target = null;\n', '    internal void ClearCombatTarget() => combat.ClearTarget();\n', 'ClearCombatTarget delegate')
write(path, text)

# Replace candidate scan: AI retains danger/approach facts; Combat owns candidate interpretation.
scan_method = r'''    private void ScanCreatures()
    {
        danger = null;
        combat.BeginCandidateScan();

        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var creatures = context?.Creatures;
        if (creatures == null) return;

        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            if (creature == fly || creature is DesertBatfly || !Valid(creature))
                continue;

            float distance = Vector2.Distance(fly.mainBodyChunk.pos, creature.mainBodyChunk.pos);
            DB_VisibilityChannel channel = creature is Player
                ? DB_VisibilityChannel.Player
                : DB_VisibilityChannel.Creature;
            if (distance > DesertBatflyTuning.SightRange ||
                !DB_VisibilityPolicy.CanObserve(
                    fly, creature.mainBodyChunk.pos, DesertBatflyTuning.SightRange, channel))
                continue;

            CreatureTemplate.Relationship relation = fly.Template.CreatureRelationship(creature.Template);
            CreatureTemplate.Relationship reverse = creature.Template.CreatureRelationship(fly.Template);
            bool predator = creature is not Player &&
                (relation.type == CreatureTemplate.Relationship.Type.Afraid ||
                 reverse.type == CreatureTemplate.Relationship.Type.Eats ||
                 reverse.type == CreatureTemplate.Relationship.Type.Attacks);

            if (predator)
            {
                float ordinaryThreatDistance = Mathf.Lerp(90f, 260f, Mathf.Clamp01(creature.TotalMass));
                float nerveScale = Mathf.Lerp(1.15f, 0.58f, fly.Personality.Nerve);
                float threatDistance = Mathf.Max(55f, ordinaryThreatDistance * nerveScale);
                if (distance < threatDistance) danger = creature;
            }

            if (creature is Player player)
            {
                bool traumatized = IsTraumatizedPlayer(player);
                bool remembered = !traumatized && IsRememberedPlayer(player);
                if (remembered && !fly.Personality.Aggressive)
                {
                    float fearDistance = Mathf.Lerp(
                        DesertBatflyTuning.GrabFearMinDistance,
                        DesertBatflyTuning.GrabFearMaxDistance,
                        fly.DesertState.GrabMemoryStrength);
                    fearDistance *= Mathf.Lerp(1.12f, 0.72f, fly.Personality.Nerve);
                    if (distance < fearDistance) danger = player;
                }

                float reactionDistance = Mathf.Lerp(125f, 78f, fly.Personality.Nerve);
                float closingThreshold = Mathf.Lerp(2.1f, 4.4f, fly.Personality.Nerve);
                int pursuitThreshold = Mathf.RoundToInt(Mathf.Lerp(16f, 44f, fly.Personality.Nerve));
                if (remembered && fly.Personality.Aggressive)
                {
                    reactionDistance *= 0.72f;
                    closingThreshold *= 1.25f;
                    pursuitThreshold = Mathf.RoundToInt(pursuitThreshold * 1.35f);
                }

                if (distance < reactionDistance)
                {
                    float closing = Vector2.Dot(
                        player.mainBodyChunk.vel,
                        Custom.DirVec(player.mainBodyChunk.pos, fly.mainBodyChunk.pos));
                    if (closing > closingThreshold) pursuit += 8;
                    else pursuit = Mathf.Max(0, pursuit - 4);
                    if (pursuit >= pursuitThreshold)
                    {
                        DisturbedByApproach(player);
                        pursuit = 0;
                    }
                }
                else
                {
                    pursuit = Mathf.Max(0, pursuit - 2);
                }
            }

            combat.ConsiderCandidate(creature, distance);
        }

        combat.CompleteCandidateScan(retreat > 0);
    }'''
replace_method(path, '    private void ScanCreatures()', scan_method)

# Remove old combat target/motivation helpers from AI.
remove_method(path, '    private bool GriefAllowsHarass()')
remove_method(path, '    private bool CanHarass(Creature creature)')
remove_method(path, '    private Player FindSocialHarassTarget()')

# Replace post-safety combat selection block with domain call.
text = read(path)
old = '''        bool retaliationReady = fly.Personality.Aggressive &&
            retaliationCharges > 0 && retaliationRecovery <= 0;
        if (fly.DesertState.Cooldown > 0 && Mode != Activity.Attach &&
            Mode != Activity.Interfere && !retaliationReady)
        {
            CancelAttack();
            SetMode(Activity.Cooldown);
            return;
        }

        if (!fly.Personality.Aggressive || !GriefAllowsHarass())
        {
            if (Mode != Activity.Roost) CancelAttack();
            TryPlanRoost();
            return;
        }

        if (!Valid(Target))
        {
            CancelAttack();
            if (memory > 0 && Valid(attacker) && CanHarass(attacker))
                Target = attacker;
            if (Target == null)
            {
                TryPlanRoost();
                return;
            }
            SetMode(Activity.Observe);
        }

        // Choosing a combat mode is state/proposal preparation only. The actual roost release,
        // steering, contact and attack timers are frozen until PrimaryOwner=Combat executes.
        if (Mode is Activity.Flight or Activity.Cooldown or Activity.Roost)
        {
            hasRoost = false;
            SetMode(Activity.Observe);
        }
'''
new = '''        DB_CombatRuntime.SelectionResult combatSelection = combat.PrepareSelection();
        if (combatSelection == DB_CombatRuntime.SelectionResult.Cooldown)
            return;
        if (combatSelection != DB_CombatRuntime.SelectionResult.Ready)
        {
            TryPlanRoost();
            return;
        }

        // Combat selected a valid target/mode, but locomotion remains frozen until Arbiter
        // actually grants PrimaryOwner=Combat.
        hasRoost = false;
'''
text = replace_once(text, old, new, 'AI combat preparation block')
# direct target clears are compatibility violations after target storage migrates
text = text.replace('            Target = null;\n', '            combat.ClearTarget();\n')
text = text.replace('                brain.Target = null;\n', '                brain.combat.ClearTarget();\n')
write(path, text)

# -----------------------------------------------------------------------------
# 3. Tests/status.
# -----------------------------------------------------------------------------
path = 'tests/DesertBatfly/Program.Task14R4.cs'
text = read(path)
insert = '''        Check(combatRuntime.GetProperty("Target", Flags) != null &&
              combatRuntime.GetMethod("BeginCandidateScan", Flags) != null &&
              combatRuntime.GetMethod("ConsiderCandidate", Flags) != null &&
              combatRuntime.GetMethod("CompleteCandidateScan", Flags) != null &&
              combatRuntime.GetMethod("PrepareSelection", Flags) != null &&
              combatRuntime.GetMethod("ArmRetaliation", Flags) != null,
            "Task14 R4 Combat runtime owns target scan/motivation and retaliation preparation");
        Check(ai.GetMethod("CanHarass", Flags) == null &&
              ai.GetMethod("FindSocialHarassTarget", Flags) == null &&
              ai.GetMethod("ArmRetaliation", Flags) == null,
            "Task14 R4 old AI shell no longer owns Harass target selection or retaliation preparation");
'''
text = replace_once(
    text,
    '        Check(combatRuntime.GetMethod("TryExecuteOwned", Flags) != null &&\n',
    insert + '        Check(combatRuntime.GetMethod("TryExecuteOwned", Flags) != null &&\n',
    'R4 B4 tests')
text = text.replace(
    'Task14 R4 B3: FlightMotor boundary is centralized and formal Combat execution/contact/AttackSlot responsibility is extracted from the AI shell; target selection/motivation remains for B4.',
    'Task14 R4 B4: Combat execution, target selection, Harass motivation and retaliation preparation are all owned by DB_CombatRuntime; final Observatory/audit closeout remains.')
write(path, text)

path = 'docs/Discussion/Task_14_R4_FlightMotorStatus.txt'
status = read(path)
status = status.replace('Revision: R4-B3 / 2026-09-07', 'Revision: R4-B4 / 2026-09-07')
status = status.replace('Status: 【R4 进行中 / B3 Combat execution/contact extraction】', 'Status: 【R4 进行中 / B4 Combat responsibility extraction complete】')
status = status.replace(
    'R4-OPEN-01 — move target discovery / Harass motivation / retaliation preparation out of DesertBatflyAI into Combat domain.\nR4-OPEN-02 — final source audit of every localGoal / sustained velocity writer after Combat selection extraction.\nR4-OPEN-03 — Observatory expose FlightMotor owner/goal/nominal speed and special-physics boundary.\nR4-OPEN-04 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.',
    'R4-B4 CLOSED — target storage, Harass candidate interpretation, social Harass candidate selection, attacker memory and retaliation preparation now live in DB_CombatRuntime. DesertBatflyAI retains only danger/approach perception and compatibility read surfaces.\n\nR4-OPEN-01 — final source audit of every localGoal / sustained velocity writer and classify every remaining special-physics exception.\nR4-OPEN-02 — Observatory expose FlightMotor owner/goal/nominal speed/modifier result and special-physics boundary.\nR4-OPEN-03 — managed compile/integration and Rain World live scenarios deferred to final validation stage per project decision.')
status += '''\n\n======================================================================\n7. R4-B4 Combat target / motivation extraction\n======================================================================\n\nDB_CombatRuntime 现拥有：Target、attacker memory、retaliation charges/recovery、Harass eligibility、social Harass candidate、target motivation 与 combat preparation。\n\nDesertBatflyAI.ScanCreatures 保留单次 RoomContext traversal，只计算 danger / player approach fear；同一可见候选通过 Combat.ConsiderCandidate 交给 Combat 域解释，避免为了分层重新扫描房间。\n\nTarget 对外仍可通过 DesertBatflyAI.Target 只读兼容面查询，但真实存储已经迁到 DB_CombatRuntime。\n'''
write(path, status)

# Final guards.
ai = read('src/Creatures/DesertBatfly/DesertBatflyAI.cs')
combat = read('src/Creatures/DesertBatfly/Combat/DB_CombatRuntime.cs')
for forbidden in ['private bool GriefAllowsHarass()', 'private bool CanHarass(Creature creature)', 'private Player FindSocialHarassTarget()', 'private void ArmRetaliation(Player player, float strength)']:
    if forbidden in ai:
        raise SystemExit(f'AI still owns B4 combat responsibility: {forbidden}')
for required in ['BeginCandidateScan', 'ConsiderCandidate', 'CompleteCandidateScan', 'PrepareSelection', 'ArmRetaliation', 'RecordAttacker', 'RecordGrabber']:
    if required not in combat:
        raise SystemExit(f'Combat runtime missing B4 surface: {required}')
if 'internal Creature Target => combat.Target;' not in ai:
    raise SystemExit('AI Target compatibility surface does not delegate to Combat runtime')
if 'combat.ConsiderCandidate(creature, distance);' not in ai:
    raise SystemExit('AI perception traversal does not feed Combat candidate interpreter')
if 'DB_CombatRuntime.SelectionResult combatSelection = combat.PrepareSelection();' not in ai:
    raise SystemExit('AI does not delegate final combat preparation to Combat runtime')

Path('scripts/task14_r4_b4_combat_selection_apply.py').unlink()
wf = Path('.github/workflows/task14-r4-b4-combat-selection-one-shot.yml')
if wf.exists(): wf.unlink()
print('R4 B4 combat target/motivation extraction prepared successfully')
