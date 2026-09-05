using System;
using System.Collections.Generic;
using System.Reflection;
using DryCycle.Creatures.DesertBatfly;
using UnityEngine;

internal static partial class Program
{
    private static void RunRoleDistribution()
    {
        int[] counts = new int[4];
        for (int seed = 0; seed < 10000; seed++)
        {
            var p = new DesertBatflyPersonality(seed);
            var scores = DesertBatflyRoleScores.Calculate(p);
            var again = DesertBatflyRoleScores.Calculate(p);
            foreach (ExpressedSocialRole role in Enum.GetValues(typeof(ExpressedSocialRole)))
            {
                float score = scores.For(role);
                Check(score == again.For(role) && score >= 0f && score <= 1f && !float.IsNaN(score), "repeatable finite role scores");
            }
            counts[(int)scores.Select(14, 0)]++;
            var grieving = DesertBatflyRoleScores.Calculate(p, 0f, 0.8f);
            Check(grieving.Bully <= scores.Bully && grieving.Opportunist <= scores.Opportunist, "grief reduces ordinary expression");
        }
        Check(counts[0] >= 7000 && counts[0] <= 8500, "None is 70-85 percent in safe baseline");
        Check(counts[1] > 0 && counts[2] > 0 && counts[3] > 0, "all three expressions possible without quotas");
        Console.WriteLine($"Role baseline (10,000): None={counts[0]}, Sentinel={counts[1]}, Bully={counts[2]}, Opportunist={counts[3]}.");
        Check(new DesertBatflyRoleScores(0.88f, 0.79f, 0.40f).Select(14, 0) == ExpressedSocialRole.None, "ambiguous tendencies stay ordinary");
        Check(new DesertBatflyRoleScores(0.40f, 0.50f, 0.35f).Select(14, 0) == ExpressedSocialRole.None, "empty budget never fills jobs");
        Check(DesertBatflyRoleScores.EntryThreshold(14, 3) > DesertBatflyRoleScores.EntryThreshold(14, 2), "budget raises entry threshold");
        Check(new DesertBatflyRoleScores(1f, 0.40f, 0.35f).Select(14, 8) == ExpressedSocialRole.Sentinel, "extreme scores can exceed soft budget");

        var seeds = new System.Random(531902);
        int ordinary = 0, min = 14, max = 0, small = 0, crowded = 0, missingSentinel = 0;
        for (int colony = 0; colony < 1000; colony++)
        {
            int expressed = 0, sentinels = 0;
            for (int i = 0; i < 14; i++)
            {
                var score = DesertBatflyRoleScores.Calculate(new DesertBatflyPersonality(seeds.Next()));
                var role = score.Select(14, expressed);
                if (role == ExpressedSocialRole.None) ordinary++;
                else expressed++;
                if (role == ExpressedSocialRole.Sentinel) sentinels++;
            }
            min = Math.Min(min, expressed); max = Math.Max(max, expressed);
            if (expressed <= 1) small++;
            if (expressed >= 5) crowded++;
            if (sentinels == 0) missingSentinel++;
        }
        Check(ordinary >= 9800 && ordinary <= 11900, "synthetic colonies preserve ordinary majority with soft pressure");
        Check(small > 0 && missingSentinel > 0 && crowded < 500, "colony composition is sparse and not guaranteed");
        Console.WriteLine($"1,000 synthetic 14-bat colonies: None={ordinary}/14000, expressed range={min}-{max}, 0-1 roles={small}, 5+ roles={crowded}, no Sentinel={missingSentinel}.");
    }

    private static void RunRoleIntegration()
    {
        Type batType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type stateType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyState", true);
        Type aiType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type rolesType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialRoles", true);
        Type roleType = mod.GetType("DryCycle.Creatures.DesertBatfly.ExpressedSocialRole", true);
        Type flockType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyFlockSnapshot", true);
        var room = Bare<Room>();
        room.abstractRoom = Bare<AbstractRoom>();
        room.abstractRoom.creatures = new List<AbstractCreature>();
        Fly Make(int seed)
        {
            var abs = Bare<AbstractCreature>();
            abs.ID = new EntityID(-1, seed);
            abs.creatureTemplate = Bare<CreatureTemplate>();
            var bat = (Fly)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(batType);
            bat.abstractPhysicalObject = abs;
            abs.realizedCreature = bat;
            abs.state = (CreatureState)Activator.CreateInstance(stateType, Flags, null, new object[] { abs }, null);
            bat.bodyChunks = new[] { new BodyChunk(bat, 0, new Vector2(50f, 50f), 6f, 0.05f) };
            bat.grabbedBy = new List<Creature.Grasp>();
            bat.grasps = new Creature.Grasp[1];
            bat.room = room;
            bat.AI = Bare<FlyAI>();
            bat.AI.behavior = FlyAI.Behavior.Idle;
            Set(bat, "DesertAI", Activator.CreateInstance(aiType, Flags, null, new object[] { bat }, null));
            room.abstractRoom.creatures.Add(abs);
            return bat;
        }
        object Brain(Fly b) => batType.GetField("DesertAI", Flags).GetValue(b);
        object Roles(Fly b) => aiType.GetField("Roles", Flags).GetValue(Brain(b));
        object Get(Fly b, string property) => rolesType.GetProperty(property, Flags).GetValue(Roles(b));
        object Call(Fly b, string method, params object[] args) => rolesType.GetMethod(method, Flags).Invoke(Roles(b), args);
        void Force(Fly b, string role) => Set(Roles(b), "<Role>k__BackingField", Enum.Parse(roleType, role));
        int Field(object value, string field) => (int)flockType.GetField(field, Flags).GetValue(value);
        object Capture(List<Fly> bats) => flockType.GetMethod("Capture", Flags).Invoke(null, new object[] { room, bats, 0.25f });
        var first = Make(911);
        var second = Make(912);
        var third = Make(913);
        var fourth = Make(914);
        first.mainBodyChunk.pos = new Vector2(20f, 40f);
        second.mainBodyChunk.pos = new Vector2(60f, 80f);
        first.mainBodyChunk.vel = new Vector2(2f, 0f);
        second.mainBodyChunk.vel = new Vector2(4f, 2f);
        third.dead = true; fourth.inShortcut = true;
        var bats = new List<Fly> { first, second, third, fourth };
        Force(first, "Bully");
        var snapshot = Capture(bats);
        Check(Field(snapshot, "ActiveCount") == 2 && Field(snapshot, "ExpressedRoleCount") == 1, "snapshot filters dead and shortcut, counts expressions");
        Check((Vector2)flockType.GetField("Center", Flags).GetValue(snapshot) == new Vector2(40f, 60f), "snapshot center");
        Check((Vector2)flockType.GetField("AverageVelocity", Flags).GetValue(snapshot) == new Vector2(3f, 1f), "snapshot average velocity");
        first.room = Bare<Room>(); second.mainBodyChunk.pos = new Vector2(float.NaN, 2f);
        var empty = Capture(bats);
        Check(Field(empty, "ActiveCount") == 0 && (Vector2)flockType.GetField("Center", Flags).GetValue(empty) == Vector2.zero, "empty and malformed snapshot finite; other-room excluded");
        first.room = room; second.mainBodyChunk.pos = new Vector2(60f, 80f);
        Check(Field(Capture(bats), "ExpressedRoleCount") == 1, "budget count recovers after transient exclusion");
        first.dead = true;
        Check(Get(first, "Expressed").ToString() == "None" && Field(Capture(bats), "ExpressedRoleCount") == 0, "dead role no longer counted immediately");
        first.dead = false;
        Call(first, "Reset");
        Check(Get(first, "Role").ToString() == "None", "room reset clears runtime expression");
        Check((int)Get(first, "EvaluationTicks") != (int)Get(second, "EvaluationTicks"), "seed-staggered first evaluation");

        void Suppressed(string expected)
        {
            Force(first, "Bully");
            Check(Get(first, "Suppression").ToString() == expected && Get(first, "Expressed").ToString() == "None", expected + " overrides expression");
            Call(first, "CheckSuppression");
            int cooldown = (int)Get(first, "Cooldown");
            Call(first, "Tick");
            Check((int)Get(first, "Cooldown") == cooldown - 1, "suppression does not refresh cooldown forever");
        }
        Set(first.State, "PlayerTraumaTicks", 5000); Set(first.State, "PlayerTraumaStrength", 0.9f);
        Suppressed("Trauma");
        Set(first.State, "PlayerTraumaTicks", 0); Set(first.State, "PlayerTraumaStrength", 0f);
        Set(first.State, "GriefStrength", 0.8f); Suppressed("Grief"); Set(first.State, "GriefStrength", 0f);
        Set(Brain(first), "retreat", 30); Suppressed("Danger"); Set(Brain(first), "retreat", 0);
        first.stun = 10; Suppressed("Unavailable"); first.stun = 0;
        first.inShortcut = true; Suppressed("Unavailable"); first.inShortcut = false;
        var holder = Bare<Player>();
        first.grabbedBy.Add(new Creature.Grasp(holder, first, 0, 0, Creature.Grasp.Shareability.NonExclusive, 1f, false));
        Suppressed("Restrained"); first.grabbedBy.Clear();
        var chainGrasp = new Creature.Grasp(second, first, 0, 0, Creature.Grasp.Shareability.NonExclusive, 1f, false);
        first.grabbedBy.Add(chainGrasp);
        first.AI.behavior = FlyAI.Behavior.Chain;
        Check(Get(first, "Suppression").ToString() == "Roost", "Fly chain grasp is roost, not generic restraint");
        first.grabbedBy.Clear();
        first.AI.behavior = FlyAI.Behavior.Idle;
        first.AI.behavior = FlyAI.Behavior.Chain; Suppressed("Roost"); first.AI.behavior = FlyAI.Behavior.Idle;
        first.AI.fleeFromRain = true; Suppressed("VanillaPriority"); first.AI.fleeFromRain = false;

        Type morale = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyIntimidation", true);
        object moraleState = morale.GetMethod("StateFor", Flags).Invoke(null, new object[] { first });
        Type revenge = morale.GetNestedType("VengeanceMode", Flags);
        Set(moraleState, "Vengeance", Enum.Parse(revenge, "Withdraw"));
        Suppressed("Vengeance");
        morale.GetMethod("Forget", Flags).Invoke(null, new object[] { first });
        moraleState = morale.GetMethod("StateFor", Flags).Invoke(null, new object[] { first });
        object fear = moraleState.GetType().GetField("PredatorFear", Flags).GetValue(moraleState);
        Set(fear, "MemoryTicks", 100); Set(fear, "Strength", 0.2f); Set(fear, "ShockTicks", 10);
        Set(moraleState, "PredatorFear", fear);
        Suppressed("Fear");
        morale.GetMethod("Forget", Flags).Invoke(null, new object[] { first });

        Force(first, "Bully");
        Set(Brain(first), "<Target>k__BackingField", second);
        Set(Brain(third), "<Target>k__BackingField", second);
        Set(Brain(fourth), "<Target>k__BackingField", second);
        third.dead = false; fourth.inShortcut = false;
        Force(third, "Bully"); Force(fourth, "Bully");
        Check((bool)aiType.GetMethod("AcquireSlot", Flags).Invoke(Brain(first), null), "bully acquires first normal slot");
        aiType.GetMethod("SetMode", Flags).Invoke(Brain(first), new[] { Enum.Parse(aiType.GetNestedType("Activity", Flags), "Approach") });
        Check((bool)aiType.GetMethod("AcquireSlot", Flags).Invoke(Brain(third), null), "bully acquires second normal slot");
        aiType.GetMethod("SetMode", Flags).Invoke(Brain(third), new[] { Enum.Parse(aiType.GetNestedType("Activity", Flags), "Circle") });
        Check(!(bool)aiType.GetMethod("AcquireSlot", Flags).Invoke(Brain(fourth), null), "third bully cannot bypass AttackSlots=2");

        // Neither Bond nor the Loner-like derived strength enters the role selection formula.
        int lonerSeed = 0;
        while (DesertBatflyRoleScores.LonerLike(new DesertBatflyPersonality(lonerSeed)) < 0.8f) lonerSeed++;
        var loner = Make(lonerSeed);
        stateType.GetMethod("StrengthenBond", Flags).Invoke(loner.State, new object[] { first.abstractCreature.ID, 0.9f });
        Check((float)stateType.GetField("SocialBondStrength", Flags).GetValue(loner.State) == 0.9f && Get(loner, "Role").ToString() == "None", "strong loner-like bond does not force a role");
        Vector2 Goal(Vector2 pos, Vector2 threat, bool predator) => (Vector2)rolesType.GetMethod("WatchGoal", Flags).Invoke(null,
            new object[] { pos, threat, Vector2.zero, predator, true });
        Vector2 playerWatch = Goal(new Vector2(190f, 0f), new Vector2(500f, 0f), false);
        Check(playerWatch.magnitude <= 260.001f && Vector2.Distance(playerWatch, new Vector2(500f, 0f)) >= 230f,
            "sentinel confirmation keeps player standoff and stays near flock");
        Check(Goal(new Vector2(300f, 0f), new Vector2(200f, 0f), false) == new Vector2(300f, 0f),
            "perimeter clamping cannot pull watcher into a nearby player");
        Vector2 predatorWatch = Goal(new Vector2(300f, 0f), new Vector2(400f, 0f), true);
        Check(Vector2.Dot(predatorWatch - new Vector2(300f, 0f), new Vector2(100f, 0f)) <= 0f,
            "perimeter correction never advances toward visible predator");
        int expressiveSeed = 0;
        while (DesertBatflyRoleScores.Calculate(new DesertBatflyPersonality(new EntityID(-1, expressiveSeed).RandomSeed)).Select(14, 0) != ExpressedSocialRole.Sentinel)
            expressiveSeed++;
        var sentinel = Make(expressiveSeed);
        object baseline = Activator.CreateInstance(flockType, Flags, null,
            new object[] { new Vector2(40f, 60f), Vector2.zero, 14, 0, 0f, 0f, 0f }, null);
        Set(Roles(sentinel), "<EvaluationTicks>k__BackingField", 0);
        Call(sentinel, "Evaluate", baseline);
        Check(Get(sentinel, "Role").ToString() == "Sentinel" && (int)Get(sentinel, "Commitment") >= 800, "real role lifecycle enters from dominant score");
        int commitment = (int)Get(sentinel, "Commitment");
        Call(sentinel, "Tick");
        Check((int)Get(sentinel, "Commitment") == commitment - 1, "role commitment ticks exactly once");
        Set(Roles(sentinel), "<Commitment>k__BackingField", 0);
        Set(Roles(sentinel), "<EvaluationTicks>k__BackingField", 0);
        Call(sentinel, "Evaluate", baseline);
        Check(Get(sentinel, "Role").ToString() == "None" && (int)Get(sentinel, "Cooldown") >= 240, "normal role ends with cooldown");
        Set(Roles(sentinel), "<EvaluationTicks>k__BackingField", 0);
        Call(sentinel, "Evaluate", baseline);
        Check(Get(sentinel, "Role").ToString() == "None", "cooldown prevents immediate reentry");
        Call(sentinel, "Reset");
        Force(sentinel, "Sentinel");
        var walker = Bare<Player>();
        walker.abstractPhysicalObject = Bare<AbstractCreature>();
        walker.bodyChunks = new[] { new BodyChunk(walker, 0, new Vector2(300f, 60f), 8f, 0.5f) };
        walker.grabbedBy = new List<Creature.Grasp>(); walker.grasps = new Creature.Grasp[2]; walker.room = room;
        void ObserveWalker()
        {
            Call(sentinel, "BeginVisibleScan");
            Call(sentinel, "ObserveVisible", walker, 250f, false);
            Call(sentinel, "EndVisibleScan", baseline);
        }
        for (int i = 0; i < 8; i++) ObserveWalker();
        Check((float)Get(sentinel, "SentinelAlertConfidence") == 0f && (int)aiType.GetField("retreat", Flags).GetValue(Brain(sentinel)) == 0,
            "quiet passerby does not automatically alarm");
        Call(sentinel, "BeginVisibleScan");
        Call(sentinel, "ObserveVisible", second, 320f, true);
        Call(sentinel, "ObserveVisible", walker, 200f, false);
        Check(rolesType.GetField("scanThreat", Flags).GetValue(Roles(sentinel)) == second,
            "nearer player cannot hide a visible predator from sentinel");
        walker.mainBodyChunk.vel = new Vector2(-3f, 0f);
        for (int i = 0; i < 3; i++) ObserveWalker();
        Check((float)Get(sentinel, "SentinelAlertConfidence") > 0f && (int)aiType.GetField("retreat", Flags).GetValue(Brain(sentinel)) == 0,
            "sentinel confirms visible approach before alarm");
        Type swarmType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertSwarmRoom", true);
        object swarm = swarmType.GetMethod("For", Flags).Invoke(null, new object[] { room });
        var hive = (FliesRoomAI)swarmType.GetField("Hive", Flags).GetValue(swarm);
        hive.flies.Add(sentinel); hive.flies.Add(loner);
        ObserveWalker();
        Check(Get(sentinel, "Role").ToString() == "None" &&
            (int)aiType.GetField("retreat", Flags).GetValue(Brain(sentinel)) > 0 &&
            (int)aiType.GetField("retreat", Flags).GetValue(Brain(loner)) == 25, "confirmed sentinel alarm reuses local escape and interrupts role");
        int alarmCooldown = (int)rolesType.GetField("alarmCooldown", Flags).GetValue(Roles(sentinel));
        for (int i = 0; i < 12; i++) ObserveWalker();
        Check((int)rolesType.GetField("alarmCooldown", Flags).GetValue(Roles(sentinel)) == alarmCooldown &&
            (float)stateType.GetField("PlayerTraumaStrength", Flags).GetValue(loner.State) == 0f,
            "suspicion never manufactures death trauma or repeats an alarm during suppression");

        var recovering = Make(28031);
        Force(recovering, "Opportunist");
        Set(Brain(recovering), "retreat", 20);
        Call(recovering, "CheckSuppression");
        Check(Get(recovering, "Role").ToString() == "None" && (int)Get(recovering, "Cooldown") >= 240 &&
            (bool)Get(recovering, "OpportunistRecoveryActive"), "danger ends Opportunist expression but preserves bounded recovery eligibility");
        for (int i = 0; i < 8; i++)
        {
            Call(recovering, "BeginVisibleScan"); Call(recovering, "EndVisibleScan", baseline);
        }
        Check(!(bool)Call(recovering, "SafeOpportunity", baseline), "retreat scans cannot pre-pay the post-danger safe window");
        Set(Brain(recovering), "retreat", 0);
        for (int i = 0; i < 4; i++)
        {
            Call(recovering, "BeginVisibleScan"); Call(recovering, "EndVisibleScan", baseline);
        }
        Check(!(bool)Call(recovering, "SafeOpportunity", baseline), "recovery still waits through 32 truly safe ticks");
        Call(recovering, "BeginVisibleScan"); Call(recovering, "EndVisibleScan", baseline);
        Check((bool)Call(recovering, "SafeOpportunity", baseline) && Get(recovering, "Role").ToString() == "None" &&
            (int)Get(recovering, "Cooldown") > 0 && (bool)Get(recovering, "OpportunistRecoveryActive"),
            "interrupted Opportunist regains early-return bias after 40 safe ticks without bypassing role cooldown");
        Set(recovering.State, "GriefStrength", 0.8f);
        Call(recovering, "CheckSuppression");
        Check(!(bool)Get(recovering, "OpportunistRecoveryActive"), "unrelated high-priority suppression invalidates stale recovery bias");
        Set(recovering.State, "GriefStrength", 0f);

        Set(Brain(sentinel), "retreat", 0);
        aiType.GetMethod("SetMode", Flags).Invoke(Brain(sentinel), new[] { Enum.Parse(aiType.GetNestedType("Activity", Flags), "Flight") });
        Force(sentinel, "Opportunist");
        for (int i = 0; i < 4; i++)
        {
            Call(sentinel, "BeginVisibleScan"); Call(sentinel, "EndVisibleScan", baseline);
        }
        Check(!(bool)Call(sentinel, "SafeOpportunity", baseline), "opportunist waits for repeated clear observations");
        Call(sentinel, "BeginVisibleScan"); Call(sentinel, "EndVisibleScan", baseline);
        Check((bool)Call(sentinel, "SafeOpportunity", baseline), "past danger plus 40 clear ticks permits cautious return");
        Set(Brain(sentinel), "retreat", 5);
        Check(!(bool)Call(sentinel, "SafeOpportunity", baseline), "opportunity cannot shorten active retreat");
        Set(Brain(sentinel), "retreat", 0);
        moraleState = morale.GetMethod("StateFor", Flags).Invoke(null, new object[] { sentinel });
        fear = moraleState.GetType().GetField("PlayerFear", Flags).GetValue(moraleState);
        Set(fear, "MemoryTicks", 100); Set(fear, "Strength", 0.08f); Set(fear, "CorpseReminderCooldown", 40);
        Set(moraleState, "PlayerFear", fear);
        Check(!(bool)Call(sentinel, "SafeOpportunity", baseline), "corpse warning blocks early return even at low fear strength");
        morale.GetMethod("Forget", Flags).Invoke(null, new object[] { sentinel });
        aiType.GetMethod("ResetRoom", Flags).Invoke(Brain(sentinel), null);
        Check(Get(sentinel, "Role").ToString() == "None" && (int)Get(sentinel, "OpportunityTicks") == 0 &&
            rolesType.GetField("visibleThreat", Flags).GetValue(Roles(sentinel)) == null, "room transition clears role and visible threat references");
        Console.WriteLine("Roles integration: snapshot validity, stagger, interruption/cooldown, PTSD/Grief/Fear/Vengeance, Bully slots, Loner-like Bond independence, Opportunist recovery.");
    }
}