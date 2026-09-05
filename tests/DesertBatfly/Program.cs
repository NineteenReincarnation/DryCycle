using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using DryCycle.Creatures.DesertBatfly;
using UnityEngine;

internal static partial class Program
{
    private static string game;
    private static Assembly mod;
    private static int assertions;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: DesertBatfly.Tests.exe <RainWorldDir> <DryCycle.dll>"); return 2; }
        game = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            mod = Assembly.LoadFrom(args[1]);
            Run();
            Console.WriteLine($"PASS: {assertions} assertions. Managed integration checks; no simulated Unity game loop.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name == "Assembly-CSharp") return Assembly.LoadFrom(Path.Combine(game, "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"));
        foreach (string folder in new[] { "RainWorld_Data/Managed", "BepInEx/core", "BepInEx/plugins", "BepInEx/utils" })
        {
            string path = Path.Combine(game, folder, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Check(DesertBatflyTuning.MealWater == 50f, "meal water cost is 50 raw points");
        Check(DesertBatflyTuning.AttackWaterPerSecond == 50f, "attached drain is 50 raw points per second");

        int males = 0;
        int sandSpitters = 0;
        int trueAvengers = 0;
        for (int seed = 0; seed < 10000; seed++)
        {
            var a = new DesertBatflyPersonality(seed);
            var b = new DesertBatflyPersonality(seed);
            Check(a.Sex == b.Sex, "stable sex");
            if (a.Sex == DesertBatflySex.Male) males++;
            var originalPersonality = new System.Random(seed);
            Check(a.PatternSeed == originalPersonality.Next() && a.SpikeSeed == originalPersonality.Next() &&
                a.Temperament == (float)originalPersonality.NextDouble(), "original personality stream preserved");
            float oldNerve = Mathf.Clamp01(Mathf.Lerp((float)new System.Random(seed ^ 0x5A17B1D3).NextDouble(), a.Temperament, 0.25f));
            float oldSand = Mathf.Clamp01((float)new System.Random(seed ^ 0x6D2B79F5).NextDouble() * 0.62f + a.Temperament * 0.28f + oldNerve * 0.10f);
            Check(a.Nerve == oldNerve && a.SandSpitAffinity == oldSand, "original nerve and sand-spit distribution unchanged");
            Check(DesertBatflyTuning.Mass * a.Size < 0.2f, "both sexes stay lightweight");
            Check(a.PatternSeed == b.PatternSeed && a.SpikeSeed == b.SpikeSeed && a.BaseColor == b.BaseColor, "stable appearance");
            Check(a.SpikeCount >= 0 && a.SpikeCount <= DesertBatflyTuning.MaxSpikes, "spike bound");
            Check(a.PatternCount >= 5 && a.PatternCount <= DesertBatflyTuning.MaxPatterns, "pattern bound");
            Check(a.Nerve >= 0f && a.Nerve <= 1f && a.RoostAffinity >= 0f && a.RoostAffinity <= 1f, "stable personality factors bounded");
            Check(a.SandSpitAffinity == b.SandSpitAffinity && a.SandSpitAffinity >= 0f && a.SandSpitAffinity <= 1f, "stable sand-spit personality factor");
            Check(a.VengeanceAffinity == b.VengeanceAffinity && a.VengeanceAffinity >= 0f && a.VengeanceAffinity <= 1f, "stable vengeance personality factor");
            Check(a.Conformity == b.Conformity && a.Conformity >= 0f && a.Conformity <= 1f, "stable independent conformity factor");
            Check(a.SocialFearScale >= 0.72f && a.SocialFearScale <= 1.48f, "social fear scale bounded");
            if (a.CanExtremeVengeance)
            {
                trueAvengers++;
                Check(a.VengeanceDrive >= 0f && a.VengeanceDrive <= 1f, "vengeance drive bounded");
            }
            if (a.CanSandSpit)
            {
                sandSpitters++;
                Check(a.SandSpitDrive >= 0f && a.SandSpitDrive <= 1f, "sand-spit drive bounded");
                Check(a.SandSpitMeterRate > 0f && a.SandSpitIntensity > 0f, "sand-spit runtime parameters positive");
            }
            if (a.Aggressive)
            {
                Check(a.FakeDiveChance >= 0.26f && a.FakeDiveChance <= 0.68f, "aggression fake-dive chance bounded");
                Check(a.RetaliationChance >= 0.38f && a.RetaliationChance <= 0.92f, "retaliation chance bounded");
            }
        }
        Check(males >= 4600 && males <= 5000, "48/52 sex distribution");
        Check(trueAvengers == 494, "exact original true avenger calibration");
        Console.WriteLine($"Sex: {males} male / {10000 - males} female; 494 true avengers expected.");
        Check(sandSpitters > 500 && sandSpitters < 9500, "sand spit is an individual minority trait, not none/all");
        Check(trueAvengers >= 450 && trueAvengers <= 550, "true-avenger rate remains approximately five percent");
        Console.WriteLine($"Personality: 10,000 repeatable seeds, {sandSpitters} sand spitters, {trueAvengers} true avengers; Conformity stable and bounded.");

        RunRoleDistribution();

        var creature = Bare<AbstractCreature>();
        creature.creatureTemplate = Bare<CreatureTemplate>();
        creature.ID = new EntityID(-1, 973);
        var state = new DesertBatflyState(creature)
        {
            Thirst = 0.74321f,
            Cooldown = 1133,
            Bites = 1,
            MealConsumed = true,
            InHive = true,
            health = 0.35f,
            GrabMemoryPlayer = 1,
            GrabMemoryStrength = 0.72f,
            GrabMemoryTicks = 2200,
            PlayerTraumaPlayer = 2,
            PlayerTraumaStrength = 0.83f,
            PlayerTraumaTicks = 6400,
            PredatorTraumaId = 177,
            PredatorTraumaStrength = 0.64f,
            PredatorTraumaTicks = 5300,
            SocialBondTarget = new EntityID(42, 177), SocialBondStrength = 0.83f,
            GriefStrength = 0.67f, GriefTicks = 2300, GriefThreatIdentity = new EntityID(-1, 333)
        };
        state.unrecognizedSaveStrings["ForeignMod"] = "preserve";
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string save = state.ToString();
            var restored = new DesertBatflyState(creature);
            restored.LoadFromString(Regex.Split(save, "<cB>"));
            Check(restored.Thirst == state.Thirst && restored.Cooldown == 1133 && restored.Bites == 1, "state round trip");
            Check(restored.InHive && restored.MealConsumed && Math.Abs(restored.health - 0.35f) < 0.001f, "hive/meal/health round trip");
            Check(restored.GrabMemoryPlayer == 1 && Math.Abs(restored.GrabMemoryStrength - 0.72f) < 0.001f && restored.GrabMemoryTicks == 2200, "grab memory round trip");
            Check(restored.PlayerTraumaPlayer == 2 && Math.Abs(restored.PlayerTraumaStrength - 0.83f) < 0.001f && restored.PlayerTraumaTicks == 6400, "player trauma round trip");
            Check(restored.PredatorTraumaId == 177 && Math.Abs(restored.PredatorTraumaStrength - 0.64f) < 0.001f && restored.PredatorTraumaTicks == 5300, "predator trauma round trip");
            Check(restored.BondStrength(new EntityID(42, 177)) == 0.83f && restored.BondStrength(new EntityID(43, 177)) == 0f, "full bond identity round trip and no number-only collision");
            Check(restored.GriefTicks == 2300 && restored.GriefStrength == 0.67f && restored.GriefThreatIdentity.Value.number == 333, "grief round trip");
            restored.TickGrief();
            Check(restored.GriefTicks == 2299 && restored.GriefStrength < 0.67f && restored.PlayerTraumaStrength == 0.83f, "grief decay leaves severe PTSD intact");
            Check(restored.HasTrauma, "restored trauma remains active");
            restored.TickTrauma();
            Check(restored.PlayerTraumaTicks == 6399 && restored.PredatorTraumaTicks == 5299, "trauma ticks decay exactly once per realized update");
            Check(restored.unrecognizedSaveStrings["ForeignMod"] == "preserve", "foreign save data preserved");
            restored.LoadFromString(new[] { "DCDesertBatflyV1<cC>973;NaN;-42;99;0" });
            Check(!float.IsNaN(restored.Thirst) && restored.Cooldown == 0 && restored.Bites == 3, "malformed save bounded");
            Check(restored.GrabMemoryPlayer == -1 && restored.GrabMemoryStrength == 0f && restored.GrabMemoryTicks == 0, "legacy payload clears optional grab memory");
            Check(restored.PlayerTraumaPlayer == -1 && restored.PlayerTraumaStrength == 0f && restored.PlayerTraumaTicks == 0, "legacy payload clears player trauma");
            Check(restored.PredatorTraumaId == int.MinValue && restored.PredatorTraumaStrength == 0f && restored.PredatorTraumaTicks == 0, "legacy payload clears predator trauma");
            Check(!restored.SocialBondTarget.HasValue && restored.GriefTicks == 0 && restored.GriefStrength == 0f, "legacy clears bond and grief");
            restored.LoadFromString(new[] { "DCDesertBatflyV1<cC>973;0.5;0;3;0;0;-1;0;0;-1;0;0;0;0;0;bad;NaN;NaN;9999;bad" });
            Check(!restored.SocialBondTarget.HasValue && restored.SocialBondStrength == 0f && restored.GriefTicks == 0 && !restored.GriefThreatIdentity.HasValue, "malformed identities and NaN sanitized");
            Check(!restored.HasTrauma, "legacy payload has no phantom PTSD");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine("State: locale-independent round trip, grab memory, persistent player/predator trauma, legacy schema, malformed data, foreign fields.");

        var bonds = new DesertBatflyState(creature);
        var friend = new EntityID(3, 20);
        var challenger = new EntityID(4, 20);
        Check(bonds.StrengthenBond(friend, 0.05f), "empty slot establishes bond");
        Check(bonds.StrengthenBond(friend, 0.05f) && bonds.SocialBondStrength == 0.10f, "same partner strengthens");
        Check(!bonds.StrengthenBond(challenger, 0.20f), "replacement margin protects bond");
        Check(bonds.StrengthenBond(challenger, 0.30f), "strong challenger replaces weak bond");
        bonds.StrengthenBond(challenger, 2f);
        Check(bonds.SocialBondStrength == 1f && !bonds.StrengthenBond(friend, 0.3f), "strong bond survives rescue challenger and clamps");
        Check(!bonds.StrengthenBond(friend, float.NaN), "invalid gain rejected");
        Check(bonds.BeginGrief(friend, null) == 0f && bonds.GriefTicks == 0, "unrelated death has no grief");
        Check(bonds.BeginGrief(challenger, friend) > 0f && bonds.GriefTicks >= 1200 && bonds.GriefTicks <= 4000, "bond death starts grief");
        int griefTicks = bonds.GriefTicks;
        Check(bonds.BeginGrief(challenger, friend) == 0f && bonds.GriefTicks == griefTicks, "duplicate death idempotent");
        for (int i = 0; i < griefTicks; i++) bonds.TickGrief();
        Check(bonds.GriefTicks == 0 && bonds.GriefStrength == 0f && !bonds.GriefThreatIdentity.HasValue, "grief expires cleanly");

        Type batType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatfly", true);
        Type aiType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyAI", true);
        Type stateType = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyState", true);
        var bat = (Fly)FormatterServices.GetUninitializedObject(batType);
        bat.abstractPhysicalObject = creature;
        creature.realizedCreature = bat;
        creature.state = (CreatureState)Activator.CreateInstance(stateType, Flags, null, new object[] { creature }, null);
        Set(creature.state, "h", -0.25f);
        bat.bodyChunks = new[] { new BodyChunk(bat, 0, new Vector2(40f, 40f), 8.5f, 0.095f) };
        bat.grabbedBy = new List<Creature.Grasp>();
        Set(bat, "DesertAI", Activator.CreateInstance(aiType, Flags, null, new object[] { bat }, null));

        Type peachPredation = mod.GetType("DryCycle.WatcherExts.PeachLizard.PeachLizardDesertBatflyPredation", true);
        MethodInfo hasEdibleRemains = peachPredation.GetMethod("HasEdibleRemains", Flags);
        bat.bites = 3;
        stateType.GetField("MealConsumed", Flags).SetValue(creature.state, false);
        Check((bool)hasEdibleRemains.Invoke(null, new object[] { bat }), "Peach scavenging accepts intact Desert Batfly remains");
        bat.bites = 1;
        Check((bool)hasEdibleRemains.Invoke(null, new object[] { bat }), "Peach scavenging accepts partially eaten remains while a bite remains");
        bat.bites = 0;
        Check(!(bool)hasEdibleRemains.Invoke(null, new object[] { bat }), "Peach scavenging rejects exhausted zero-bite remains");
        bat.bites = 1;
        stateType.GetField("MealConsumed", Flags).SetValue(creature.state, true);
        Check(!(bool)hasEdibleRemains.Invoke(null, new object[] { bat }), "Peach scavenging rejects MealConsumed remains even if runtime bites are inconsistent");
        bat.bites = 3;
        stateType.GetField("MealConsumed", Flags).SetValue(creature.state, false);
        Console.WriteLine("Peach scavenging: intact and partial corpses remain food; exhausted/consumed remains are rejected.");

        var rock = Bare<Rock>();
        rock.abstractPhysicalObject = Bare<AbstractPhysicalObject>();
        var rockChunk = new BodyChunk(rock, 0, Vector2.zero, 3f, 0.1f);
        for (int i = 0; i < 100; i++)
            bat.Violence(rockChunk, new Vector2(1f, 0f), bat.mainBodyChunk, null, Creature.DamageType.Blunt, 2f, 45f);
        Check(!bat.dead && ((HealthState)creature.state).health == -0.25f, "100 rocks cannot kill or accumulate damage, even pre-injured");
        Check(bat.stun >= 110 && bat.mainBodyChunk.vel.x > 0f, "rock retains stun and impulse");
        Console.WriteLine("Weapon: actual compiled Violence override, 100 hits against injured state.");

        Set(bat, "mealFood", 2);
        var edible = (IPlayerEdible)bat;
        Check(edible.FoodPoints == 2, "derived interface food dispatch");
        Type hooks = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyHooks", true);
        var nourishment = hooks.GetMethod("Nourishment", Flags);
        On.SlugcatStats.orig_NourishmentOfObjectEaten original = SlugcatStats.NourishmentOfObjectEaten;
        Check((int)nourishment.Invoke(null, new object[] { original, SlugcatStats.Name.White, edible }) == 8, "Survivor 2 food");
        Check((int)nourishment.Invoke(null, new object[] { original, SlugcatStats.Name.Red, edible }) == 4, "Hunter 1 food");
        On.SlugcatStats.orig_NourishmentOfObjectEaten forbidden = (name, food) => -1;
        Check((int)nourishment.Invoke(null, new object[] { forbidden, SlugcatStats.Name.White, edible }) == -1, "inedible diet preserved");
        var vanillaFly = Bare<Fly>();
        Check((int)nourishment.Invoke(null, new object[] { original, SlugcatStats.Name.White, vanillaFly }) == 4, "vanilla food unchanged");
        Console.WriteLine("Nutrition: actual vanilla nourishment routine and derived interface dispatch.");

        var sandType = mod.GetType("DryCycle.TerrainExt.QuicksandZone.QuicksandSurface", true);
        var contact = sandType.GetMethod("TryGetContact", Flags);
        var top = new[] { new Vector2(0f, 100f), new Vector2(100f, 100f) };
        var bottom = new[] { new Vector2(0f, 0f), new Vector2(100f, 0f) };
        bool Sand(float x, float y) => (bool)contact.Invoke(null, new object[] { new Vector2(x, y), 22f, top, bottom, null });
        Check(Sand(50f, 50f) && Sand(50f, 100f) && Sand(-15f, 100f), "sand interior/surface/edge rejected");
        Check(!Sand(250f, 100f), "distant curve remains eligible");
        Console.WriteLine("Quicksand: actual shared geometry query, interior/surface/edge vs distant geometry.");

        var room = Bare<Room>();
        room.abstractRoom = Bare<AbstractRoom>();
        room.abstractRoom.creatures = new List<AbstractCreature>();
        Fly MakeBat(int id)
        {
            var abs = Bare<AbstractCreature>();
            abs.ID = new EntityID(-1, id);
            abs.creatureTemplate = creature.creatureTemplate;
            var instance = (Fly)FormatterServices.GetUninitializedObject(batType);
            instance.abstractPhysicalObject = abs;
            abs.realizedCreature = instance;
            abs.state = (CreatureState)Activator.CreateInstance(stateType, Flags, null, new object[] { abs }, null);
            instance.bodyChunks = new[] { new BodyChunk(instance, 0, new Vector2(50f, 50f), 8.5f, 0.095f) };
            instance.grabbedBy = new List<Creature.Grasp>();
            instance.room = room;
            Set(instance, "DesertAI", Activator.CreateInstance(aiType, Flags, null, new object[] { instance }, null));
            room.abstractRoom.creatures.Add(abs);
            return instance;
        }
        object Brain(Fly instance) => batType.GetField("DesertAI", Flags).GetValue(instance);
        object Invoke(object brain, string name, params object[] arguments) => aiType.GetMethod(name, Flags).Invoke(brain, arguments);
        void Mode(object brain, string name) => Invoke(brain, "SetMode", Enum.Parse(aiType.GetNestedType("Activity", Flags), name));
        var target = MakeBat(100);
        var first = MakeBat(101);
        var second = MakeBat(102);
        var third = MakeBat(103);
        Type social = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflySocialBond", true);
        object Social(string name, params object[] arguments) => social.GetMethod(name, Flags).Invoke(null, arguments);
        first.AI = Bare<FlyAI>();
        second.AI = Bare<FlyAI>();
        first.AI.behavior = second.AI.behavior = FlyAI.Behavior.Chain;
        first.grasps = new Creature.Grasp[1];
        second.grasps = new Creature.Grasp[1];
        var chainGrasp = new Creature.Grasp(second, first, 0, 0, Creature.Grasp.Shareability.NonExclusive, 1f, false);
        second.grasps[0] = chainGrasp;
        first.grabbedBy.Add(chainGrasp);
        Social("SampleChain", first);
        Social("SampleChain", second);
        Check(Math.Abs((float)Social("GetBondStrength", first, second) - 0.004f) < 0.00001f &&
              Math.Abs((float)Social("GetBondStrength", second, first) - 0.004f) < 0.00001f, "real direct chain neighbours gain slowly in both directions");
        first.grabbedBy.Clear();
        second.grasps[0] = null;
        first.AI.behavior = second.AI.behavior = FlyAI.Behavior.Idle;
        Social("OnSuccessfulRescue", first, second);
        float rescuedBond = (float)Social("GetBondStrength", second, first);
        float rescuerBond = (float)Social("GetBondStrength", first, second);
        Check(rescuedBond > rescuerBond && rescuerBond > 0f, "real rescue helper creates asymmetric bonds");
        Social("AddBond", first, second, 0.1f);
        Check((float)Social("GetBondStrength", second, first) == rescuedBond, "one direction never mirrors");
        second.room = Bare<Room>();
        Social("AddBond", first, second, 0.5f);
        Check(Math.Abs((float)Social("GetBondStrength", first, second) - rescuerBond - 0.1f) < 0.00001f, "cross-room event cannot grow bond");
        second.room = room;
        second.dead = true;
        Social("AddBond", second, first, 0.5f);
        Check((float)Social("GetBondStrength", second, first) == rescuedBond, "dead observer cannot grow bond");
        Social("AddBond", first, second, 0.5f);
        Set(first.State, "SocialBondStrength", 0.9f);
        Set(first.State, "PlayerTraumaStrength", 0.9f);
        Set(first.State, "PlayerTraumaTicks", 5000);
        second.room = Bare<Room>();
        Social("OnBondPartnerDeath", first, second, null);
        Check((int)stateType.GetField("GriefTicks", Flags).GetValue(first.State) == 0, "cross-room death is not perceived");
        second.room = room;
        Set(Brain(first), "hasSlot", true);
        Set(Brain(first), "retaliationCharges", 2);
        Set(Brain(first), "memory", 100);
        Social("OnBondPartnerDeath", first, second, null);
        Check(!(bool)aiType.GetField("hasSlot", Flags).GetValue(Brain(first)) &&
            (int)aiType.GetField("retaliationCharges", Flags).GetValue(Brain(first)) == 0 &&
            (int)aiType.GetField("memory", Flags).GetValue(Brain(first)) == 0, "grief clears old attack slot and retaliation");
        int observedGrief = (int)stateType.GetField("GriefTicks", Flags).GetValue(first.State);
        Check(observedGrief >= 1200 && (float)stateType.GetField("PlayerTraumaStrength", Flags).GetValue(first.State) == 0.9f, "observed death creates grief without clearing severe PTSD");
        Social("OnBondPartnerDeath", first, second, null);
        Check((int)stateType.GetField("GriefTicks", Flags).GetValue(first.State) == observedGrief, "repeated death helper has no second grief");
        Social("OnBondPartnerDeath", third, second, null);
        Check((int)stateType.GetField("GriefTicks", Flags).GetValue(third.State) == 0, "unrelated observer receives no grief");
        second.dead = false;
        Set(first.State, "GriefStrength", 0f);
        Set(first.State, "GriefTicks", 0);
        Console.WriteLine("Social: full identity, single-slot replacement, asymmetric rescue, death idempotence, grief expiry, cross-room/dead guards.");
        foreach (var instance in new[] { first, second, third }) Set(Brain(instance), "<Target>k__BackingField", target);
        Check((bool)Invoke(Brain(first), "AcquireSlot"), "first attack slot");
        Mode(Brain(first), "RetaliationCharge");
        Check((bool)Invoke(Brain(second), "AcquireSlot"), "second attack slot");
        Mode(Brain(second), "Interfere");
        Check(!(bool)Invoke(Brain(third), "AcquireSlot"), "third attacker blocked while retaliation uses slots");
        first.stun = 10;
        Invoke(Brain(first), "TickMemory");
        Check((bool)Invoke(Brain(third), "AcquireSlot"), "stun releases slot");
        third.grabbedBy.Add(new Creature.Grasp(target, third, 0, 0, Creature.Grasp.Shareability.NonExclusive, 1f, false));
        Mode(Brain(third), "Attach");
        Invoke(Brain(third), "AfterPhysics", true);
        Check(aiType.GetProperty("Mode", Flags).GetValue(Brain(third)).ToString() != "Attach", "held attacker detaches before drain");
        Check((float)stateType.GetField("Thirst", Flags).GetValue(third.State) > 0f, "grab cannot satisfy thirst");
        Check(!(bool)aiType.GetProperty("FormalAttack", Flags).GetValue(Brain(third)), "grab releases formal attack slot");
        Console.WriteLine("Attack slots: retaliation shares cap at two, stun release, held-attach cancellation.");

        room.Width = room.Height = 30;
        room.Tiles = new Room.Tile[30, 30];
        for (int x = 0; x < 30; x++)
        for (int y = 0; y < 30; y++)
            room.Tiles[x, y] = new Room.Tile(x, y, Room.Tile.TerrainType.Air, false, false, false, 0, 0);
        room.terrain = new TerrainManager();
        var curve = Bare<TerrainCurve>();
        curve.startX = 0f;
        curve.endX = 600f;
        curve.segmentWidth = 10f;
        curve.segments = 61;
        curve.bottom = -100f;
        curve.collisionPoints = new Vector2[61];
        for (int i = 0; i <= 60; i++) curve.collisionPoints[i] = new Vector2(i * 10f, 100f);
        room.terrain.terrainList.Add(curve);
        var sandGeometry = new List<(Vector2[], Vector2[])> { (top, bottom) };
        Type emergence = mod.GetType("DryCycle.Creatures.DesertBatfly.DesertBatflyEmergence", true);
        var validPath = emergence.GetMethod("ValidPath", Flags);
        bool Path(float x, Vector2 outward) => (bool)validPath.Invoke(null, new object[] { room, sandGeometry, new Vector2(x, 100f), outward });
        Check(Path(300f, Vector2.up), "real terrain accepts clear outward emergence");
        Check(!Path(50f, Vector2.up) && !Path(115f, Vector2.up), "full emergence path rejects sand and margin");
        Check(!Path(300f, Vector2.down), "inward normal rejected by real collision geometry");
        room.Tiles[15, 7].Terrain = Room.Tile.TerrainType.Solid;
        Check(!Path(300f, Vector2.up), "blocked exit corridor rejected");
        Console.WriteLine("Emergence: actual TerrainManager/TerrainCurve collision, full path, outward normal, obstruction and sand margin.");
        RunRoleIntegration();
    }

    private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Flags).SetValue(obj, value);
    private static void Check(bool passed, string description)
    {
        assertions++;
        if (!passed) throw new Exception("FAIL: " + description);
    }
}
