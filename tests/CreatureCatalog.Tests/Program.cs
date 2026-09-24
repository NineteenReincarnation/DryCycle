using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using LanceScavenger.Tests;

// Runs the deployed frontend's actual request/cache code and the installed game's alias and
// CreatureSymbol implementations. Only Unity native calls are blocked by the existing fixture.
// Raster publication is supplied here; this does not claim GPU readback or game UI coverage.
internal static class Program
{
    private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static string gameDirectory, frontendDirectory;
    private static Type picker;
    private static int assertions;

    private static int Main(string[] args)
    {
        gameDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "D:/Steam/steamapps/common/Rain World");
        frontendDirectory = Path.Combine(gameDirectory, "RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins");
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try
        {
            Prepare(); Run();
            Console.WriteLine("PASS: " + assertions + " assertions; deployed creature icon requests, game aliases, shared cache and sprite/color selection.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Prepare() => LanceManagedUnityFixture.Load(gameDirectory);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        picker = Assembly.LoadFrom(Path.Combine(frontendDirectory, "DryCycle.DevTool.RWImGui.dll"))
            .GetType("DryCycle.DevUI.DevTool.RWImGui.WorldCreatureCatalogPicker", true);
        object snapshot = New("CatalogSnapshot");
        IDictionary byId = (IDictionary)Get(snapshot, "ById");
        foreach (var type in new[] { CreatureTemplate.Type.YellowLizard, CreatureTemplate.Type.BlueLizard,
            CreatureTemplate.Type.LanternMouse, CreatureTemplate.Type.DaddyLongLegs })
        {
            object entry = New("CreatureEntry");
            Set(entry, "Id", type.value); Set(entry, "Type", type); Set(entry, "SourceFingerprint", "fixture");
            byId.Add(type.value, entry);
        }
        picker.GetField("catalog", Fields).SetValue(null, snapshot);
        picker.GetField("currentCatalogValidated", Fields).SetValue(null, true);
        StaticWorld.creatureTemplates = Array.Empty<CreatureTemplate>();

        foreach (var pair in new[] { ("Yellow", "YellowLizard"), ("Blue", "BlueLizard"),
            ("Mouse", "LanternMouse"), ("Daddy", "DaddyLongLegs"), ("  yElLoW  ", "YellowLizard") })
        {
            ClearIcons();
            Check(Request(pair.Item1).State == "Pending", "Unprepared alias queues a request: " + pair.Item1);
            string requested = Dequeue();
            Check(requested == pair.Item1.Trim(), "Whitespace is normalized before queuing.");
            Check(!(bool)Call("ResolveIconRequestMainThread", requested, null), "Aliases redirect to the canonical icon request.");
            Check((string)Call("ResolveIconIdForRender", pair.Item1) == pair.Item2, "Uses WorldLoader's real alias mapping.");
            Check(Dequeue() == pair.Item2, "Only the canonical icon is built.");
            object[] arguments = { pair.Item2, null };
            Check((bool)Call("ResolveIconRequestMainThread", arguments), "Canonical requests reach the icon builder.");
            var type = (CreatureTemplate.Type)Get(arguments[1], "Type");
            var symbol = new IconSymbol.IconSymbolData(type, AbstractPhysicalObject.AbstractObjectType.Creature, 0);
            string sprite = CreatureSymbol.SpriteNameOfCreature(symbol);
            Check(sprite != "Futile_White", "The game resolves an actual creature sprite.");
            if (type == CreatureTemplate.Type.YellowLizard)
            {
                var tint = CreatureSymbol.ColorOfCreature(symbol);
                Check(sprite == "Kill_Yellow_Lizard" && tint.r == 1f && Math.Abs(tint.g - 0.6f) < 0.001f && tint.b == 0f,
                    "Yellow uses the game's orange-yellow lizard icon, not a loading placeholder.");
            }

            object raster = PublishRaster(pair.Item2, sprite);
            var aliased = Request(pair.Item1);
            Check(aliased.State == "Ready" && ReferenceEquals(aliased.Raster, raster), "The row receives the canonical ready raster.");
            Check(ReferenceEquals(Request(pair.Item2).Raster, raster) && Dequeue() == null, "Repeated rows share a raster without rebuilding.");
            var icons = (IList)Get(Call("CapturePersistentSnapshot"), "Icons");
            Check(icons.Count == 1 && (string)Get(icons[0], "CreatureId") == pair.Item2, "Only canonical raster data is persisted.");
            Call("InvalidateSourceIcons", new[] { pair.Item2 }, false);
            Check(Request(pair.Item1).State == "Pending" && Dequeue() == pair.Item2, "Source invalidation refreshes aliases too.");
        }

        ClearIcons();
        Request("Yellow"); Request("YellowLizard");
        Call("ResolveIconRequestMainThread", Dequeue(), null);
        Check(Dequeue() == "YellowLizard" && Dequeue() == null, "Simultaneous alias and catalog requests are deduplicated.");

        Request("MissingCreatureForIconTest");
        Call("ResolveIconRequestMainThread", Dequeue(), null);
        Check(Request("MissingCreatureForIconTest").State == "Failed", "Unknown names leave Pending and become an observable failure.");
        Check(Dequeue() == null, "Unknown names do not enqueue work every frame.");
    }

    private static (object Raster, string State) Request(string id)
    {
        object[] args = { id, null, null };
        Call("TryGetIconForRender", args);
        return (args[1], args[2].ToString());
    }
    private static string Dequeue()
    {
        object[] args = { null };
        return (bool)Call("TryDequeueIcon", args) ? (string)args[0] : null;
    }
    private static object PublishRaster(string id, string sprite)
    {
        object raster = New("IconRaster"), slot = New("IconSlot");
        Set(raster, "Available", true); Set(raster, "SpriteName", sprite);
        Set(slot, "State", Enum.Parse(picker.GetNestedType("IconState", BindingFlags.NonPublic), "Ready"));
        Set(slot, "Raster", raster); Set(slot, "Validated", true);
        ((IDictionary)picker.GetField("iconSlots", Fields).GetValue(null))[id] = slot;
        return raster;
    }
    private static void ClearIcons()
    {
        foreach (string name in new[] { "iconSlots", "iconAliases", "iconRequests", "queuedIcons" })
        {
            object collection = picker.GetField(name, Fields).GetValue(null);
            collection.GetType().GetMethod("Clear").Invoke(collection, null);
        }
    }
    private static object New(string name) => Activator.CreateInstance(picker.GetNestedType(name, BindingFlags.NonPublic), true);
    private static object Get(object target, string field) => target.GetType().GetField(field, Fields).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Fields).SetValue(target, value);
    private static object Call(string name, params object[] args) => picker.GetMethod(name, Fields).Invoke(null, args);
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name == "Assembly-CSharp") return Assembly.LoadFrom(Path.Combine(gameDirectory, "BepInEx/utils/PUBLIC-Assembly-CSharp.dll"));
        foreach (string directory in new[] { frontendDirectory, Path.Combine(gameDirectory, "RainWorld_Data/Managed"),
            Path.Combine(gameDirectory, "BepInEx/core"), Path.Combine(gameDirectory, "BepInEx/plugins"),
            Path.GetFullPath(Path.Combine(gameDirectory, "../../workshop/content/312520/3417372413/plugins")) })
        {
            string file = Path.Combine(directory, name + ".dll");
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        return null;
    }
}
