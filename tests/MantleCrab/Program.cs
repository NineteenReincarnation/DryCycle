using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Web.Script.Serialization;
using DryCycle.Creatures.MantleCrab;
using UnityEngine;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static string game, output;
    private static Assembly mod;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.WriteLine("Usage: MantleCrab.Tests.exe <RainWorldDir> <artifact-directory>"); return 2; }
        game = args[0]; output = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
        {
            string name = new AssemblyName(eventArgs.Name).Name;
            if (name == "Assembly-CSharp") name = "PUBLIC-Assembly-CSharp";
            foreach (string folder in new[] { "RainWorld_Data/Managed", "BepInEx/utils", "BepInEx/core", "BepInEx/plugins", "RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins" })
            {
                string path = Path.Combine(game, folder, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Run(); Console.WriteLine("PASS " + assertions + " checks. Managed math/mesh validation; no in-game collision loop."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static Type Type(string name) => mod.GetType("DryCycle.Creatures.MantleCrab." + name, true);
    private static object Get(object instance, string name) => instance.GetType().GetField(name, Flags).GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Flags).SetValue(instance, value);
    private static object Call(Type type, string method, object instance, params object[] args) => type.GetMethod(method, Flags).Invoke(instance, args);
    private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static object Phenotype(int seed, float dominance = .5f)
    {
        AbstractCreature creature = Empty<AbstractCreature>(); creature.ID = new EntityID(-1, seed);
        creature.personality = new AbstractCreature.Personality { energy = .5f, bravery = .5f, sympathy = .5f,
            dominance = dominance, nervous = .5f, aggression = .5f };
        object genome = Activator.CreateInstance(Type("Rendering.MantleCrabVisualGenome"), Flags, null, new object[] { creature }, null);
        return Activator.CreateInstance(Type("Rendering.MantleCrabVisualPhenotype"), Flags, null, new[] { genome }, null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        mod = typeof(MantleCrab).Assembly;
        Directory.CreateDirectory(output);
        Type genome = Type("Rendering.MantleCrabVisualGenome"), math = Type("MantleCrabRigMath");
        for (int seed = -100; seed < 100; seed++)
        {
            float before = (float)Call(genome, "Hash", null, seed, "ShellHue");
            Call(genome, "Hash", null, seed, "NewUnrelatedChannel");
            Check(before == (float)Call(genome, "Hash", null, seed, "ShellHue"), "Named channels shifted");
            object a = Phenotype(seed), b = Phenotype(seed), large = Phenotype(seed, 1f);
            foreach (FieldInfo field in a.GetType().GetFields(Flags).Where(f => f.FieldType == typeof(float)))
                Check(field.GetValue(a).Equals(field.GetValue(b)), "Phenotype reload drift: " + field.Name);
            Check((float)Get(a, "ShellWidth") >= .96f && (float)Get(a, "ShellWidth") <= 1.04f, "Species width escaped bounds");
            Check(Get(a, "Hue").Equals(Get(large, "Hue")), "Dominance must not change color");
        }
        System.Random random = new(91);
        float[] lengths = { 71, 119, 78, 40 };
        float maxTipError = 0f;
        for (int pose = 0; pose < 500; pose++)
        {
            Vector2 anchor = new(0, 280), target = new((float)random.NextDouble() * 100 - 50, (float)random.NextDouble() * 140);
            Vector2[] points = { new(-40, 220), new(-60, 105), new(-40, 40), target };
            Call(math, "Solve", null, anchor, target, lengths, points);
            Vector2 previous = anchor;
            for (int i = 0; i < 4; i++)
            {
                Check(Math.Abs(Vector2.Distance(previous, points[i]) - lengths[i]) < .005f, "Rigid segment length drift");
                previous = points[i];
            }
            maxTipError = Math.Max(maxTipError, Vector2.Distance(points[3], target));
            Check(Vector2.Distance(points[3], target) < 1.2f, "Reachable tip failed contact: " + Vector2.Distance(points[3], target));
        }
        for (int feet = 0; feet <= 4; feet++)
        {
            float h = 240, velocity = 0;
            for (int tick = 0; tick < 600; tick++)
            {
                velocity -= .9f;
                velocity += feet * (float)Call(math, "SupportAcceleration", null, 280f - h, velocity + .9f, .9f, feet);
                h += velocity;
            }
            if (feet >= 2) Check(Math.Abs(velocity) < .001f && Math.Abs(h - 280) < .1f, "Support did not settle with " + feet + " feet");
            else Check(h < 0, "Unsupported/one-foot body floats");
        }
        Console.WriteLine("Worst reachable IK tip error: " + maxTipError);
        Export(Phenotype(1729));
    }

    private static void Export(object phenotype)
    {
        MantleCrab crab = Empty<MantleCrab>();
        Set(crab, "Phenotype", phenotype);
        BodyChunk[] chunks = new BodyChunk[5];
        Vector2[] rest = (Vector2[])Type("MantleCrab").GetField("ShellRest", Flags).GetValue(null);
        for (int i = 0; i < 5; i++) { chunks[i] = Empty<BodyChunk>(); chunks[i].pos = chunks[i].lastPos = rest[i] + new Vector2(0, 280); }
        crab.bodyChunks = chunks;
        Type limbType = Type("MantleCrabLimb");
        for (int pincer = 0; pincer < 2; pincer++)
        {
            Array limbs = Array.CreateInstance(limbType, pincer == 1 ? 2 : 4);
            for (int i = 0; i < limbs.Length; i++)
            {
                object limb = Activator.CreateInstance(limbType, Flags, null, new object[] { i, pincer == 1 }, null);
                limbs.SetValue(limb, i);
                Vector2 anchor = chunks[(int)Get(limb, "AnchorChunk")].pos + Vector2.down * (pincer == 1 ? 14 : 8);
                if (pincer == 1) anchor.x += (float)Get(limb, "Side") * 16;
                Call(limbType, "Reset", limb, anchor);
                Vector2 target = anchor + new Vector2((float)Get(limb, "Side") * (pincer == 1 ? 15 : i < 2 ? 32 : 12),
                    pincer == 1 ? -240 : -275);
                if (pincer == 0) target.y = 0;
                Vector2[] points = (Vector2[])Get(limb, "Pos");
                Call(limbType, "SolvePose", limb, anchor, target, pincer == 0);
                Array.Copy(points, (Vector2[])Get(limb, "LastPos"), 4);
            }
            Set(crab, pincer == 1 ? "Pincers" : "Legs", limbs);
        }
        MantleCrabGraphics graphics = new(crab);
        Array parts = (Array)Get(graphics, "parts");
        RoomCamera.SpriteLeaser leaser = Empty<RoomCamera.SpriteLeaser>(); leaser.sprites = new FSprite[parts.Length];
        List<object> meshes = new();
        for (int i = 0; i < parts.Length; i++)
        {
            object part = parts.GetValue(i);
            int columns = (int)Get(part, "Columns"), rows = (int)Get(part, "Rows");
            TriangleMesh mesh = Empty<TriangleMesh>();
            mesh.vertices = new Vector2[(columns + 1) * (rows + 1)]; leaser.sprites[i] = mesh;
        }
        Call(typeof(MantleCrabGraphics), "PoseSprites", graphics, leaser, 1f, Vector2.zero);
        int[] order = Enumerable.Range(0, 16).Concat(Enumerable.Range(32, 7)).Concat(Enumerable.Range(16, 16)).Concat(Enumerable.Range(39, 22)).ToArray();
        foreach (int i in order)
        {
            object part = parts.GetValue(i); TriangleMesh mesh = (TriangleMesh)leaser.sprites[i];
            foreach (Vector2 point in mesh.vertices) Check(!float.IsNaN(point.x) && !float.IsNaN(point.y), "Nonfinite mesh");
            meshes.Add(new { columns = Get(part, "Columns"), rows = Get(part, "Rows"), kind = (int)Get(part, "Kind"),
                depth = Get(part, "Depth"), vertices = mesh.vertices.Select(v => new { x = v.x, y = v.y }).ToArray() });
        }
        Func<string, float> f = name => (float)Get(phenotype, name);
        Vector4 noise = (Vector4)Get(phenotype, "NoiseSeed");
        var fixture = new { motif = new[] { f("MotifScale"), f("MotifSharpness"), f("MotifContrast"), f("MotifWarp") },
            pattern = new[] { f("Fragmentation"), f("StripePhase"), f("Curvature"), f("Asymmetry") },
            material = new[] { f("ChitinRoughness"), f("LegDetail"), f("PincerAccent"), f("Hue") },
            eye = new[] { f("EyeCore"), f("EyeWarmth"), 0f, 0f }, noise = new[] { noise.x, noise.y, noise.z, noise.w }, meshes };
        File.WriteAllText(Path.Combine(output, "fixture.json"), new JavaScriptSerializer { MaxJsonLength = 4000000 }.Serialize(fixture));
        using (BinaryWriter writer = new(File.Create(Path.Combine(output, "cpu-atlas.rgba"))))
        {
            MethodInfo sample = Type("Rendering.MantleCrabMaterialBaker").GetMethod("Sample", Flags);
            for (int y = 0; y < 256; y++)
            for (int x = 0; x < 896; x++)
            {
                object[] args = { phenotype, Enum.ToObject(Type("Rendering.MantleCrabMaterial"), x / 128),
                    new Vector2(x % 128 / 127f, y % 128 / 127f), null, null };
                sample.Invoke(null, args); Color color = (Color)args[y < 128 ? 3 : 4];
                foreach (float channel in new[] { color.r, color.g, color.b, color.a })
                    writer.Write((byte)Math.Round(Math.Max(0, Math.Min(1, channel)) * 255));
            }
        }
    }
}
