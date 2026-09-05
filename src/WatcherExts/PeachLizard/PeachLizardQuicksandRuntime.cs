using DryCycle.TerrainExt.QuicksandZone;
using RWCustom;
using UnityEngine;
using Watcher;

namespace DryCycle.WatcherExts.PeachLizard;

/// <summary>
/// Bridges Peach Lizard's native sand-burrowing AI to the ordinary-sand material
/// portions of DryCycle QuicksandZone.
///
/// No teleport, custom tunnel state or custom body motion is used here. We only:
///  1) remove Peach's swimmer-only dry-ground surcharge on verified safe custom Sand,
///  2) let its native LurkTracker score those Sand cells as valid hiding places, and
///  3) occasionally feed room-wide safe Sand candidates into that same LurkTracker.
///
/// Once the native path points at AItile.Accessibility.Sand, Lizard.Update owns
/// burrowUpcoming, BodyChunk.burrow/buried, TerrainManager burrow traversal, sand
/// puffs, scrape/dig sounds and natural emergence again.
/// </summary>
internal static class PeachLizardQuicksandRuntime
{
    private const int CandidateRefreshTicks = 120;
    private const int CandidateSamples = 8;
    private const float CandidateImprovement = 1.5f;

    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        On.LizardAI.TravelPreference += LizardAI_TravelPreference;
        On.LizardAI.LurkTracker.LurkPosScore += LurkTracker_LurkPosScore;
        On.LizardAI.LurkTracker.Update += LurkTracker_Update;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;

        On.LizardAI.TravelPreference -= LizardAI_TravelPreference;
        On.LizardAI.LurkTracker.LurkPosScore -= LurkTracker_LurkPosScore;
        On.LizardAI.LurkTracker.Update -= LurkTracker_Update;
        PeachLizardQuicksandSandMap.Reset();
    }

    private static PathCost LizardAI_TravelPreference(
        On.LizardAI.orig_TravelPreference orig,
        LizardAI self,
        MovementConnection connection,
        PathCost cost)
    {
        PathCost result = orig(self, connection, cost);
        Lizard lizard = self?.lizard;
        Room room = lizard?.room;

        if (!IsPeach(lizard) || room?.aimap == null ||
            !connection.destinationCoord.TileDefined ||
            connection.destinationCoord.room != room.abstractRoom.index ||
            !PeachLizardQuicksandSandMap.TryGetSafeSand(
                room,
                connection.destinationCoord,
                out _))
            return result;

        // Peach is both Swimmer and Burrower. Vanilla LizardAI checks Swimmer first,
        // adds +5 to every dry destination, and therefore never reaches the generic
        // Burrower preference branch. On our verified ordinary-sand cells, undo only
        // that exact swimmer surcharge. All base costs, legality, threats and other
        // mods remain intact.
        if (!room.GetTile(connection.destinationCoord).AnyWater)
            result.resistance = Mathf.Max(0f, result.resistance - 5f);

        // Recreate the useful depth preference from vanilla's Burrower branch for
        // this custom sand only: while travelling far through sand, a Peach Lizard
        // prefers being properly under the surface instead of skimming along it.
        WorldCoordinate destination = self.pathFinder.GetDestination;
        if (destination != default && room.terrain != null &&
            connection.startCoord.TileDefined &&
            Custom.ManhattanDistance(connection.destinationCoord, destination) > 5)
        {
            int surfaceTileY = Mathf.FloorToInt(
                room.terrain.SnapToTerrain(
                    room.MiddleOfTile(connection.destinationCoord),
                    room.MiddleOfTile(connection.startCoord)).y / 20f);

            if (connection.destinationCoord.y <= surfaceTileY)
            {
                result.resistance += Mathf.Lerp(
                    7f,
                    0f,
                    Mathf.Min(surfaceTileY - connection.destinationCoord.y, 4) / 4f);
            }
        }

        return result;
    }

    private static float LurkTracker_LurkPosScore(
        On.LizardAI.LurkTracker.orig_LurkPosScore orig,
        LizardAI.LurkTracker self,
        WorldCoordinate testLurkPos)
    {
        Lizard lizard = self?.lizard;
        Room room = lizard?.room;
        if (!IsPeach(lizard) || room?.aimap == null ||
            !PeachLizardQuicksandSandMap.TryGetSafeSand(
                room,
                testLurkPos,
                out float depth))
            return orig(self, testLurkPos);

        // This mirrors the safety gates at the top of vanilla LurkPosScore. We only
        // bypass Peach's swimmer-specific "Floor or DeepWater only" rejection for a
        // cell already proven to be safe DryCycle Sand.
        if (!room.aimap.TileAccessibleToCreature(testLurkPos.Tile, lizard.Template) ||
            room.GetTile(testLurkPos).Terrain == Room.Tile.TerrainType.Slope ||
            testLurkPos.room != lizard.abstractCreature.pos.room ||
            !self.AI.pathFinder.CoordinateReachable(testLurkPos) ||
            !self.AI.pathFinder.CoordinatePossibleToGetBackFrom(testLurkPos))
            return -100000f;

        AItile aiTile = room.aimap.getAItile(testLurkPos);
        if (aiTile == null || aiTile.acc != AItile.Accessibility.Sand)
            return -100000f;

        // Native dry swimmer lurk positions sit around +20. Give real sand a similar
        // baseline, with a modest preference for being partially buried rather than
        // exactly at the skin or at the deepest bottom of the zone.
        float depthComfort = 1f - Mathf.Clamp01(Mathf.Abs(depth - 0.42f) / 0.58f);
        float score = 20f + depthComfort * 8f;

        score -= aiTile.visibility / 1000f;
        for (int i = 0; i < 8; i++)
        {
            IntVector2 lookTile = testLurkPos.Tile + Custom.eightDirections[i] * 10;
            if (room.VisualContact(testLurkPos.Tile, lookTile))
                score += room.aimap.getAItile(lookTile).visibility / 8000f;
        }

        if (aiTile.narrowSpace)
            score -= 10000f;

        // Preserve vanilla's mild social spacing contribution so a custom sand
        // destination still behaves like a normal LurkTracker choice around other
        // large creatures instead of becoming an unconditional magic tunnel target.
        if (self.AI.tracker != null)
        {
            for (int i = 0; i < self.AI.tracker.CreaturesCount; i++)
            {
                Tracker.CreatureRepresentation representation = self.AI.tracker.GetRep(i);
                if (representation == null ||
                    representation.BestGuessForPosition().room != testLurkPos.room ||
                    representation.representedCreature?.creatureTemplate == null ||
                    representation.representedCreature.creatureTemplate.smallCreature ||
                    (representation.dynamicRelationship != null &&
                     representation.dynamicRelationship.currentRelationship.type ==
                         CreatureTemplate.Relationship.Type.Eats))
                    continue;

                float distance = representation.BestGuessForPosition().Tile.FloatDist(testLurkPos.Tile);
                if (distance < 20f &&
                    representation.representedCreature.creatureTemplate.bodySize >=
                    lizard.Template.bodySize * 0.8f)
                    score += distance / 10f;
            }
        }

        return score;
    }

    private static void LurkTracker_Update(
        On.LizardAI.LurkTracker.orig_Update orig,
        LizardAI.LurkTracker self)
    {
        orig(self);

        Lizard lizard = self?.lizard;
        Room room = lizard?.room;
        if (!IsPeach(lizard) || lizard.safariControlled || room?.aimap == null ||
            room.game == null || !room.readyForAI)
            return;

        int phase = PositiveModulo(lizard.abstractCreature.ID.RandomSeed, CandidateRefreshTicks);
        if (PositiveModulo(room.game.clock + phase, CandidateRefreshTicks) != 0)
            return;

        int candidateCount = PeachLizardQuicksandSandMap.CandidateCount(room);
        if (candidateCount == 0) return;

        float currentScore = self.LurkPosScore(self.lurkPosition);
        WorldCoordinate best = self.lurkPosition;
        float bestScore = currentScore;
        int cycle = room.game.clock / CandidateRefreshTicks;
        int seed = lizard.abstractCreature.ID.RandomSeed ^ (cycle * 486187739);

        // Sample only a handful of cached columns. This lets a Peach discover safe
        // sand anywhere in the room without an every-frame/full-room search.
        int samples = Mathf.Min(CandidateSamples, candidateCount);
        for (int i = 0; i < samples; i++)
        {
            if (!PeachLizardQuicksandSandMap.TryGetCandidate(
                    room,
                    seed + i * 104729,
                    out WorldCoordinate candidate))
                continue;

            float candidateScore = self.LurkPosScore(candidate);
            if (candidateScore > bestScore)
            {
                bestScore = candidateScore;
                best = candidate;
            }
        }

        if (best.TileDefined &&
            (currentScore <= 0f || bestScore > currentScore + CandidateImprovement))
        {
            self.lurkPosition = best;
            self.lookPosition = best.Tile;
            self.bestVisLook = 0;
        }
    }

    private static bool IsPeach(Lizard lizard)
    {
        return ModManager.Watcher &&
               lizard?.Template != null &&
               lizard.Template.type == WatcherEnums.CreatureTemplateType.PeachLizard;
    }

    private static int PositiveModulo(int value, int modulus)
    {
        if (modulus <= 0) return 0;
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
