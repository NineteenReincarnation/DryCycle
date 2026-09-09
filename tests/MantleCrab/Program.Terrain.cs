using System;
using DryCycle.Creatures.MantleCrab;
using UnityEngine;

internal static partial class Program
{
    private static void TerrainTests()
    {
        ProbeTerrainTests();
        SpawnSupportTests();
    }

    private static void ProbeTerrainTests()
    {
        Room room = CreateRoom(24, 24);

        Vector2 flat = FindSurface(room, new Vector2(240f, 290f), new Vector2(240f, 20f), 295f, out Vector2 flatNormal);
        Check(Math.Abs(flat.y - 21f) < .01f, "Flat ground height changed");
        Check(Vector2.Dot(flatNormal, Vector2.up) > .999f, "Flat ground normal changed");
        terrainCases++;

        room.Tiles[12, 1].Terrain = Room.Tile.TerrainType.Solid;
        Vector2 step = FindSurface(room, new Vector2(250f, 290f), new Vector2(250f, 40f), 295f, out _);
        Check(Math.Abs(step.y - 41f) < .01f, "Step ground height changed");
        terrainCases++;

        room.Tiles[11, 1].Terrain = Room.Tile.TerrainType.Slope;
        Vector2 slope = FindSurface(room, new Vector2(230f, 290f), new Vector2(230f, 30f), 295f, out Vector2 slopeNormal);
        Check(Math.Abs(slope.y - 31f) < .01f, "Slope sample height changed");
        Check(Math.Abs(slopeNormal.x) > .5f && slopeNormal.y > .5f, "Slope normal was not returned");
        terrainCases++;

        room.Tiles[11, 1].Terrain = Room.Tile.TerrainType.Air;
        Check(!MantleCrabTerrainProbe.StillSupported(room, slope), "Removed ledge remains planted");
        terrainCases++;

        bool unreachable = MantleCrabTerrainProbe.Find(
            room,
            new Vector2(100f, 450f),
            new Vector2(100f, 20f),
            200f,
            out _,
            out _);
        Check(!unreachable, "Unreachable floor supports body");
        terrainCases++;
    }

    private static Vector2 FindSurface(Room room, Vector2 anchor, Vector2 desired, float reach, out Vector2 normal)
    {
        bool found = MantleCrabTerrainProbe.Find(room, anchor, desired, reach, out Vector2 point, out normal);
        Check(found, "Terrain target not found near " + desired);
        Check(MantleCrabTerrainProbe.StillSupported(room, point), "New terrain contact was not stable");
        return point;
    }

    private static void SpawnSupportTests()
    {
        Room flatRoom = CreateRoom();
        MantleCrab flatCrab = Empty<MantleCrab>();
        flatCrab.room = flatRoom;
        int planted = 0;

        for (int i = 0; i < 4; i++)
        {
            MantleCrabLimb leg = new(i, false);
            Vector2 anchor = new(
                320f + leg.Rest[0].x,
                21f - leg.RestTipOffset.y);
            leg.Reset(anchor);
            if (leg.TrySnapToSupport(flatCrab, anchor))
            {
                planted++;
                Check(leg.Planted, "Successful spawn snap did not mark leg planted");
                Check(Math.Abs(leg.Tip.y - 21f) < 1.2f, "Spawn-snapped foot missed flat ground");
                Check(Vector2.Dot(leg.GroundNormal, Vector2.up) > .999f, "Flat spawn support returned a tilted normal");
            }
        }
        Check(planted == 4, "Normal flat-ground spawn should immediately plant all four legs; planted=" + planted);
        spawnSupportCases++;

        Room highRoom = CreateRoom();
        MantleCrab highCrab = Empty<MantleCrab>();
        highCrab.room = highRoom;
        MantleCrabLimb highLeg = new(0, false);
        Vector2 highAnchor = new(320f, 560f);
        highLeg.Reset(highAnchor);
        Check(!highLeg.TrySnapToSupport(highCrab, highAnchor), "Airborne spawn incorrectly snapped to unreachable ground");
        Check(!highLeg.Planted, "Airborne spawn became planted without reachable terrain");
        spawnSupportCases++;

        Room slopeRoom = CreateRoom(24, 24);
        slopeRoom.Tiles[11, 1].Terrain = Room.Tile.TerrainType.Slope;
        MantleCrab slopeCrab = Empty<MantleCrab>();
        slopeCrab.room = slopeRoom;
        MantleCrabLimb slopeLeg = new(0, false);
        Vector2 desired = new(230f, 31f);
        Vector2 slopeAnchor = desired - slopeLeg.RestTipOffset;
        slopeLeg.Reset(slopeAnchor);
        Check(slopeLeg.TrySnapToSupport(slopeCrab, slopeAnchor), "Slope spawn failed to acquire reachable support");
        Check(slopeLeg.Planted, "Slope spawn support was not planted");
        Check(Math.Abs(slopeLeg.GroundNormal.x) > .5f && slopeLeg.GroundNormal.y > .5f,
            "Slope spawn did not preserve terrain normal");
        spawnSupportCases++;
    }
}
