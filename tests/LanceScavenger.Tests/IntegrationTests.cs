using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DryCycle.Creatures.LanceScavenger;
using DryCycle.Framework.Creature.Core;
using DryCycle.Items.ScavengerLance;
using DryCycle.Registration;
using IteratorFramework.Tests;
using UnityEngine;
using LanceCreature = DryCycle.Creatures.LanceScavenger.LanceScavenger;
using static LanceScavenger.Tests.Program;

namespace LanceScavenger.Tests;

internal static class IntegrationTests
{
    internal static void SaveRoundTrips()
    {
        ScavengerLanceHooks.Enable(); ItemRegistry.Enable();
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var original = new AbstractScavengerLance(null, new WorldCoordinate(3, 4, 5, -1), new EntityID(-1, 209), 87.5f)
            { rippleLayer = 4, unrecognizedAttributes = new[] { "another-mod=data" } };
            string save = original.ToString();
            var parsed = SaveState.AbstractPhysicalObjectFromString(null, save) as AbstractScavengerLance;
            Check(parsed != null && parsed.type.value == "ScavengerLance", "Registry restores custom item, not a vanilla spear");
            Check(parsed.Length == 87.5f && parsed.ID == original.ID && parsed.pos == original.pos && parsed.rippleLayer == 4,
                "Length, ID, position and ripple layer round-trip independently of culture");
            Check(parsed.ToString() == save && parsed.unrecognizedAttributes[0] == "another-mod=data", "Foreign item save attributes survive");
            var corrupt = AbstractScavengerLance.Parse(null, ItemSaveData.Parse(save.Replace("length=87.5", "length=NaN")));
            Check(corrupt.Length == LanceCombatMath.DefaultLength, "Nonfinite save length gets a safe default");
            Check(ScavengerLanceDevConsoleSupport.ParseLength(new[] { "length=90" }) == 90f, "Console uses culture-independent length");
            Throws(() => ScavengerLanceDevConsoleSupport.ParseLength(new[] { "length=NaN" }));
            Throws(() => ScavengerLanceDevConsoleSupport.ParseLength(new[] { "length=150" }));
            Throws(() => ScavengerLanceDevConsoleSupport.ParseLength(new[] { "broken=yes" }));

            AbstractCreature abstractCreature = Abstract(CreatureTemplate.Type.Scavenger);
            var state = new LanceScavengerState(abstractCreature) { GearIssued = true, health = 0.55f };
            abstractCreature.state = state;
            state.unrecognizedSaveStrings["foreignFlag"] = "keep";
            string saved = state.ToString();
            var restored = new LanceScavengerState(abstractCreature);
            restored.LoadFromString(saved.Split(new[] { "<cB>" }, StringSplitOptions.RemoveEmptyEntries));
            Check(restored.GearIssued && Math.Abs(restored.health - 0.55f) < 0.001f, "Birth ownership and health survive load");
            Check(restored.unrecognizedSaveStrings["foreignFlag"] == "keep" && restored.socialMemory != null, "Social and foreign creature state preserved");
            Check(restored.ToString() == saved, "Repeated creature serialization does not duplicate birth markers");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; ItemRegistry.Disable(); ScavengerLanceHooks.Disable(); }
    }

    internal static void Lanes()
    {
        RuntimeScene scene = Scene();
        LanceCreature lance = Add<LanceCreature>(scene, new Vector2(100, 90), 0.85f);
        ProbeCreature target = Add<ProbeCreature>(scene, new Vector2(350, 90), 0.8f);
        Check(ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Clear, "Clear supported horizontal lane accepted");
        target.mainBodyChunk.vel = new Vector2(3,0);
        Check(ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Aim.x > target.mainBodyChunk.pos.x, "Lane leads a moving target");
        target.mainBodyChunk.vel = Vector2.zero;
        target.mainBodyChunk.pos.y = 170;
        Check(!ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Clear, "Vertical target rejected");
        target.mainBodyChunk.pos.y = 90;
        Scavenger friend = Add<Scavenger>(scene, new Vector2(220,90), 0.85f);
        Check(ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Reason == "friend in lane", "Scavenger allies block initiation");
        scene.AbstractRoom.creatures.Remove(friend.abstractCreature);
        scene.Room.Tiles[12,4].Terrain = Room.Tile.TerrainType.Solid;
        Check(!ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Clear, "Wall blocks body and tip corridor");
        scene.Room.Tiles[12,4].Terrain = Room.Tile.TerrainType.Air;
        scene.Room.Tiles[12,5].Terrain = Room.Tile.TerrainType.Solid;
        Check(!ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Clear, "Low ceiling blocks lane");
        scene.Room.Tiles[12,5].Terrain = Room.Tile.TerrainType.Air;
        for (int x = 10; x <= 16; x++) for (int y = 0; y < 3; y++) scene.Room.Tiles[x,y].Terrain = Room.Tile.TerrainType.Air;
        Check(ChargeLanePlanner.Evaluate(lance, lance.mainBodyChunk.pos, target).Reason == "unsafe landing", "Deep unsupported gap rejected");
    }

    internal static void WeaponContacts()
    {
        ScavengerLanceHooks.Enable();
        try
        {
            RuntimeScene scene = Scene();
            ProbeCreature holder = Add<ProbeCreature>(scene, new Vector2(120,90), 0.85f);
            ProbeCreature victim = Add<ProbeCreature>(scene, new Vector2(190,90), 0.85f);
            ScavengerLance weapon = Weapon(scene, holder);
            Set(weapon, "_previousTip", new Vector2(160,90));
            Set(weapon, "_previousGrip", new Vector2(107,90));
            Set(weapon, "_gripValid", true);
            Set(weapon, "_grip", new LanceGrip(new Vector2(147,90), Vector2.right, true, true, 120f));
            holder.mainBodyChunk.vel = new Vector2(19,0);
            Invoke(weapon, "ResolveTip", new Vector2(200,90), 1f, true, 19f);
            Check(victim.Hits == 1 && victim.Damage > 1f && holder.Impacts == 1, "Actual weapon tip dispatches damage and wielder recoil");
            Invoke(weapon, "ResolveTip", new Vector2(200,90), 1f, true, 19f);
            Check(victim.Hits == 1, "A creature is hit once per attack even with multiple sweeps");
            Check(holder.mainBodyChunk.vel.x > 0f && holder.mainBodyChunk.vel.x < 19f, "Impact retains meaningful forward inertia");

            victim.mainBodyChunk.pos = victim.mainBodyChunk.lastPos = new Vector2(150,91);
            victim.Hits = 0;
            Set(weapon, "_previousGrip", new Vector2(120,90));
            Set(weapon, "_thrustFrames", 8);
            Invoke(weapon, "ResolveShaft");
            Check(victim.Hits == 0 && victim.mainBodyChunk.vel.sqrMagnitude > 0f, "Shaft pushes without piercing damage");
            victim.mainBodyChunk.pos = victim.mainBodyChunk.lastPos = new Vector2(210,90);
            victim.mainBodyChunk.vel = Vector2.zero;
            Set(weapon, "_hitCreatures", new HashSet<Creature>());
            Set(weapon, "_previousTip", new Vector2(180,90));
            scene.Room.Tiles[9,4].Terrain = Room.Tile.TerrainType.Solid;
            Invoke(weapon, "ResolveTip", new Vector2(220,90), 1f, true, 19f);
            Check(victim.Hits == 0, "Tip cannot damage a creature through terrain");
            scene.Room.Tiles[9,4].Terrain = Room.Tile.TerrainType.Air;
            int impacts = holder.Impacts;
            Invoke(weapon, "ResolveTerrain", holder, 19f, true);
            Check(holder.Impacts == impacts + 1 && holder.LastWall, "Wall collision reaches the wielder's failure response");

            // Birth provisioning must not replace a stolen/dropped weapon on realization.
            LanceCreature scavenger = Add<LanceCreature>(scene, new Vector2(300,90), 0.85f);
            scavenger.abstractCreature.state = new LanceScavengerState(scavenger.abstractCreature) { GearIssued = true };
            int count = scene.AbstractRoom.entities.Count;
            Invoke(scavenger, "EnsureBirthLance"); Invoke(scavenger, "EnsureBirthLance");
            Check(scene.AbstractRoom.entities.Count == count, "Already issued equipment never respawns");
            ((LanceScavengerState)scavenger.abstractCreature.state).GearIssued = false;
            scavenger.abstractCreature.spawnData = "{disarmed}";
            Invoke(scavenger, "EnsureBirthLance");
            Check(((LanceScavengerState)scavenger.abstractCreature.state).GearIssued && scene.AbstractRoom.entities.Count == count,
                "Console disarmed state is persistent rather than waiting to spawn a weapon");
        }
        finally { ScavengerLanceHooks.Disable(); }
    }

    internal static void DevConsoleLifecycle()
    {
        Type spawner = Type.GetType("DevConsole.ObjectSpawner, DevConsole", true);
        IDictionary objects = (IDictionary)spawner.GetField("safeObjSpawners", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        IDictionary creatures = (IDictionary)spawner.GetField("safeCritSpawners", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        LanceScavengerDefinition.Register();
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                ScavengerLanceHooks.Enable(); ScavengerLanceHooks.Enable();
                ScavengerLanceDevConsoleSupport.TryRegister(); ScavengerLanceDevConsoleSupport.TryRegister();
                Check(objects.Contains(ScavengerLanceHooks.ObjectType), "Console registers exactly one usable item spawner");
                object info = objects[ScavengerLanceHooks.ObjectType];
                Check(info != null, "Console has a concrete spawner");
                CreatureDevConsoleSupport.TryRegisterAll(); CreatureDevConsoleSupport.TryRegisterAll();
                Check(creatures.Contains(LanceScavengerDefinition.Type), "Creature registry exposes LanceScavenger to DevConsole");
                ScavengerLanceDevConsoleSupport.ResetRegistration();
                Check(!objects.Contains(ScavengerLanceHooks.ObjectType), "PreModsInit/reset removes our item spawner");
                CreatureDevConsoleSupport.ResetRegistration();
                Check(!creatures.Contains(LanceScavengerDefinition.Type), "Creature spawner reset permits reinitialization");
                var oldType = ScavengerLanceHooks.ObjectType;
                ScavengerLanceHooks.Disable(); ScavengerLanceHooks.Disable();
                Check(oldType.Index < 0, "Item ExtEnum is unregistered on disable");
            }
        }
        finally { CreatureDevConsoleSupport.ResetRegistration(); ScavengerLanceHooks.Disable(); }
    }

    internal static RuntimeScene Scene()
    {
        RuntimeScene scene = RuntimeScene.Create("LANCE_TEST");
        RuntimeScene.Set(scene.Room, "Width", 50); RuntimeScene.Set(scene.Room, "Height", 14);
        scene.Room.Tiles = new Room.Tile[50,14];
        for (int x = 0; x < 50; x++) for (int y = 0; y < 14; y++)
            scene.Room.Tiles[x,y] = new Room.Tile(x,y, y < 3 ? Room.Tile.TerrainType.Solid : Room.Tile.TerrainType.Air, false,false,false,0,0);
        scene.Room.lightSources = new List<LightSource>();
        return scene;
    }
    internal static AbstractCreature Abstract(CreatureTemplate.Type type)
    {
        var creature = RuntimeScene.Raw<AbstractCreature>();
        creature.creatureTemplate = RuntimeScene.Raw<CreatureTemplate>();
        creature.creatureTemplate.type = type;
        creature.creatureTemplate.meatPoints = 2;
        creature.creatureTemplate.socialMemory = true;
        creature.state = new HealthState(creature);
        creature.stuckObjects = new List<AbstractPhysicalObject.AbstractObjectStick>();
        return creature;
    }
    internal static T Add<T>(RuntimeScene scene, Vector2 position, float mass) where T : Creature
    {
        T creature = RuntimeScene.Raw<T>();
        creature.abstractPhysicalObject = Abstract(typeof(Scavenger).IsAssignableFrom(typeof(T)) ? CreatureTemplate.Type.Scavenger : CreatureTemplate.Type.Slugcat);
        creature.abstractCreature.realizedObject = creature;
        creature.abstractCreature.world = scene.World;
        creature.abstractCreature.pos = scene.Room.GetWorldCoordinate(position);
        creature.bodyChunks = new[] { new BodyChunk(creature,0,position,6f,mass) };
        creature.grabbedBy = new List<Creature.Grasp>();
        creature.grasps = new Creature.Grasp[3];
        creature.room = scene.Room;
        scene.AbstractRoom.creatures.Add(creature.abstractCreature);
        return creature;
    }
    internal static ScavengerLance Weapon(RuntimeScene scene, ProbeCreature holder = null)
    {
        var weapon = RuntimeScene.Raw<ScavengerLance>();
        weapon.abstractPhysicalObject = new AbstractScavengerLance(scene.World, new WorldCoordinate(0,7,4,-1), new EntityID(-1,10));
        weapon.abstractPhysicalObject.realizedObject = weapon;
        weapon.bodyChunks = new[] { new BodyChunk(weapon,0,new Vector2(147,90),3f,0.34f) };
        weapon.grabbedBy = new List<Creature.Grasp>();
        weapon.rotation = weapon.lastRotation = Vector2.right;
        weapon.room = scene.Room;
        Set(weapon, "_hitCreatures", new HashSet<Creature>());
        Set(weapon, "_shaftContacts", new Dictionary<Creature,int>());
        if (holder != null) weapon.grabbedBy.Add(new Creature.Grasp(holder,weapon,0,0,Creature.Grasp.Shareability.NonExclusive,0.5f,false));
        return weapon;
    }
    internal static void Set(object target, string name, object value) => RuntimeScene.Set(target, name, value);
    internal static object Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
}

internal sealed class ProbeCreature : Creature, ILanceWielder
{
    internal int Hits, Impacts;
    internal float Damage;
    internal bool LastWall;
    private ProbeCreature(AbstractCreature creature, World world) : base(creature, world) { }
    public override void Violence(BodyChunk source, Vector2? force, BodyChunk chunk, Appendage.Pos appendage, DamageType type, float damage, float stun)
    { Hits++; Damage += damage; }
    public bool TryGetLanceGrip(ScavengerLance lance, out LanceGrip grip) { grip = default; return false; }
    public void LanceImpact(bool wall, float speed, float retainedSpeed) { Impacts++; LastWall = wall; }
}
