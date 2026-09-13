using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace IteratorFramework.Tests;

/// <summary>Engine-owned types with a small managed room graph. No Unity process, renderer or real save is started.</summary>
internal sealed class RuntimeScene
{
    internal RainWorldGame Game;
    internal World World;
    internal AbstractRoom AbstractRoom;
    internal Room Room;

    internal static RuntimeScene Create(string name, bool story = false)
    {
        var scene = new RuntimeScene { Game = Raw<RainWorldGame>(), World = Raw<World>(), AbstractRoom = Raw<AbstractRoom>() };
        scene.Game.session = story ? (GameSession)Raw<StoryGameSession>() : Raw<SandboxGameSession>();
        scene.Game.session.game = scene.Game;
        scene.Game.session.Players = new List<AbstractCreature>();
        Set(scene.Game, "<cameras>k__BackingField", Array.Empty<RoomCamera>());
        scene.World.name = "TEST";
        scene.World.singleRoomWorld = true;
        Set(scene.World, "<game>k__BackingField", scene.Game);
        scene.AbstractRoom.name = name;
        scene.AbstractRoom.index = 0;
        scene.AbstractRoom.world = scene.World;
        scene.AbstractRoom.entities = new List<AbstractWorldEntity>();
        scene.AbstractRoom.entitiesInDens = new List<AbstractWorldEntity>();
        scene.AbstractRoom.creatures = new List<AbstractCreature>();
        scene.World.abstractRooms = new[] { scene.AbstractRoom };
        scene.World.activeRooms = new List<Room>();
        scene.World.unloadingRooms = new List<Room>();
        scene.Realize();
        return scene;
    }

    internal void Realize()
    {
        Room = Raw<Room>();
        Set(Room, "<abstractRoom>k__BackingField", AbstractRoom);
        Room.game = Game;
        Room.world = World;
        Set(Room, "Width", 10);
        Set(Room, "Height", 10);
        Room.Tiles = new Room.Tile[10, 10];
        for (int x = 0; x < 10; x++)
        for (int y = 0; y < 10; y++)
            Room.Tiles[x, y] = new Room.Tile(x, y, Room.Tile.TerrainType.Air, false, false, false, 0, 0);
        Room.updateList = new List<UpdatableAndDeletable>();
        Room.drawableObjects = new List<IDrawable>();
        Room.physicalObjects = new[] { new List<PhysicalObject>(), new List<PhysicalObject>(), new List<PhysicalObject>() };
        Room.loadingProgress = 3;
        Room.gravity = 0.73f;
        Set(Room, "updateIndex", -1);
        AbstractRoom.realizedRoom = Room;
    }

    internal static T Raw<T>() where T : class => (T)FormatterServices.GetUninitializedObject(typeof(T));

    internal Player AddPlayer(bool dead, bool inShortcut)
    {
        var abstractPlayer = Raw<AbstractCreature>();
        var player = Raw<Player>();
        player.abstractPhysicalObject = abstractPlayer;
        abstractPlayer.realizedObject = player;
        player.room = Room;
        player.inShortcut = inShortcut;
        Set(player, "<dead>k__BackingField", dead);
        Game.session.Players.Add(abstractPlayer);
        return player;
    }

    internal static void Set(object target, string name, object value)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) continue;
            field.SetValue(target, value);
            return;
        }
        throw new MissingFieldException(target.GetType().FullName, name);
    }
}
