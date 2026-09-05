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

internal static class Program
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

        int sandSpitters = 0;
        int trueAvengers = 0;
        for (int seed = 0; seed < 10000; seed++)
        {
            var a = new DesertBatflyPersonality(seed);
            var b = new DesertBatflyPersonality(seed);
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
        Check(sandSpitters > 500 && sandSpitters < 9500, "sand spit is an individual minority trait, not none/all");
        Check(trueAvengers >= 450 && trueAvengers <= 550, "true-avenger rate remains approximately five percent");
        Console.WriteLine($"Personality: 10,000 repeatable seeds, {sandSpitters} sand spitters, {trueAvengers} true avengers; Conformity stable and bounded.");

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
            PredatorTraumaTicks = 5300
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
            Check(restored.HasTrauma, "restored trauma remains active");
            restored.TickTrauma();
            Check(restored.PlayerTraumaTicks == 6399 && restored.PredatorTraumaTicks == 5299, "trauma ticks decay exactly once per realized update");
            Check(restored.unrecognizedSaveStrings["ForeignMod"] == "preserve", "foreign save data preserved");
            restored.LoadFromString(new[] { "DCDesertBatflyV1<cC>973;NaN;-42;99;0" });
            Check(!float.IsNaN(restored.Thirst) && restored.Cooldown == 0 && restored.Bites == 3, "malformed save bounded");
            Check(restored.GrabMemoryPlayer == -1 && restored.GrabMemoryStrength == 0f && restored.GrabMemoryTicks == 0, "legacy payload clears optional grab memory");
            Check(restored.PlayerTraumaPlayer == -1 && restored.PlayerTraumaStrength == 0f && restored.PlayerTraumaTicks == 0, "legacy payload clears player trauma");
            Check(restored.PredatorTraumaId == int.MinValue && restored.PredatorTraumaStrength == 0f && restored.PredatorTraumaTicks == 0, "legacy payload clears predator trauma");
            Check(!restored.HasTrauma, "legacy payload has no phantom PTSD");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Console.WriteLine("State: locale-independent round trip, grab memory, persistent player/predator trauma, legacy schema, malformed data, foreign fields.");

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
    }

    private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Flags).SetValue(obj, value);
    private static void Check(bool passed, string description)
    {
        assertions++;
        if (!passed) throw new Exception("FAIL: " + description);
    }
}
