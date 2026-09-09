using System;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static int assertions;
    private static int genomeCases;
    private static int walkingIkPoses;
    private static int pincerIkPoses;
    private static int terrainCases;
    private static int standingCases;
    private static int spawnSupportCases;
    private static int platformSurfaceCases;
    private static int geometryMeshes;
    private static int atlasPixels;

    private static T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo field = instance.GetType().GetField(name, Flags);
        if (field == null)
            throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
    }

    private static object GetField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, Flags);
        if (field == null)
            throw new MissingFieldException(instance.GetType().FullName, name);
        return field.GetValue(instance);
    }

    private static Room CreateRoom(int width = 32, int height = 32)
    {
        Room room = Empty<Room>();
        room.Width = width;
        room.Height = height;
        room.Tiles = new Room.Tile[width, height];

        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            Room.Tile.TerrainType terrain = y == 0
                ? Room.Tile.TerrainType.Solid
                : Room.Tile.TerrainType.Air;
            room.Tiles[x, y] = new Room.Tile(x, y, terrain, false, false, false, 0, 0);
        }

        return room;
    }
}
