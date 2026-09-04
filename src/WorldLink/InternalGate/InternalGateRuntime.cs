using System;
using System.Collections.Generic;
using System.IO;

namespace DryCycle.WorldLink.InternalGate;

/// <summary>
/// Lets a vanilla karma gate connect two rooms inside the same loaded region.
///
/// No world.txt tag is required. A room listed in World/Gates/locks.txt is promoted into
/// WorldLoader.gatesList during world mapping, which gives it a normal AbstractRoom.gateIndex
/// and therefore the complete vanilla WaterGate/ElectricGate runtime. Once realized, a gate is
/// treated as internal only when two distinct connections resolve inside the currently loaded World.
/// </summary>
internal static class InternalGateRuntime
{
    private const string LocksRelativePath = "World/Gates/locks.txt";

    private static readonly HashSet<string> DeclaredGateRooms = new(StringComparer.OrdinalIgnoreCase);
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        ReloadGateDeclarations();

        _enabled = true;
        On.WorldLoader.MappingRooms += WorldLoader_MappingRooms;
        On.RegionGate.customKarmaGateRequirements += RegionGate_CustomKarmaGateRequirements;
        On.OverWorld.GateRequestsSwitchInitiation += OverWorld_GateRequestsSwitchInitiation;
        On.RegionGate.Update += RegionGate_Update;

        Plugin.Logger?.LogInfo(
            $"InternalGate: automatic karma-gate registration enabled; " +
            $"{DeclaredGateRooms.Count} locks.txt room(s) can work without world.txt GATE/InternalGate tags.");
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.WorldLoader.MappingRooms -= WorldLoader_MappingRooms;
        On.RegionGate.customKarmaGateRequirements -= RegionGate_CustomKarmaGateRequirements;
        On.OverWorld.GateRequestsSwitchInitiation -= OverWorld_GateRequestsSwitchInitiation;
        On.RegionGate.Update -= RegionGate_Update;

        DeclaredGateRooms.Clear();
        _enabled = false;
    }

    /// <summary>
    /// Vanilla's world.txt GATE tag only contributes the current room index to gatesList.
    /// Mirror that step for every room declared in locks.txt. The rest of the gate pipeline
    /// stays vanilla: gateIndex, IsGateRoom(), WaterGate/ElectricGate, graphics and persistence.
    /// </summary>
    private static void WorldLoader_MappingRooms(On.WorldLoader.orig_MappingRooms orig, WorldLoader self)
    {
        int roomCounter = -1;
        string roomName = null;

        if (_enabled && TryGetCurrentRoomName(self, out roomName) && DeclaredGateRooms.Contains(roomName))
        {
            // MappingRooms increments rmcntr internally, so capture it before calling vanilla.
            roomCounter = self.rmcntr;
        }

        orig(self);

        if (roomCounter < 0 || self?.world == null)
        {
            return;
        }

        int roomIndex = roomCounter + self.world.firstRoomIndex;
        if (!self.gatesList.Contains(roomIndex))
        {
            self.gatesList.Add(roomIndex);
            Plugin.Logger?.LogDebug(
                $"InternalGate: auto-registered '{roomName}' as a vanilla gate from locks.txt.");
        }
    }

    private static void RegionGate_CustomKarmaGateRequirements(
        On.RegionGate.orig_customKarmaGateRequirements orig,
        RegionGate self)
    {
        if (_enabled && IsInternalGate(self?.room?.abstractRoom) && self.karmaRequirements != null)
        {
            for (int i = 0; i < self.karmaRequirements.Length && i < 2; i++)
            {
                self.karmaRequirements[i] ??= RegionGate.GateRequirement.OneKarma;
            }
        }

        orig(self);
    }

    private static void OverWorld_GateRequestsSwitchInitiation(
        On.OverWorld.orig_GateRequestsSwitchInitiation orig,
        OverWorld self,
        RegionGate reportBackToGate)
    {
        if (!_enabled || !IsInternalGate(reportBackToGate?.room?.abstractRoom))
        {
            orig(self, reportBackToGate);
            return;
        }

        MarkGatePassed(reportBackToGate);

        // Both sides already live in the active World. Do not infer another region from the
        // room name and do not create an OverWorld WorldLoader.
        Plugin.Logger?.LogDebug(
            $"InternalGate: '{reportBackToGate.room.abstractRoom.name}' stayed inside region " +
            $"'{reportBackToGate.room.world?.region?.name ?? reportBackToGate.room.world?.name ?? "?"}'.");
    }

    private static void RegionGate_Update(On.RegionGate.orig_Update orig, RegionGate self, bool eu)
    {
        bool internalGate = _enabled && IsInternalGate(self?.room?.abstractRoom);

        orig(self, eu);

        if (internalGate && self.waitingForWorldLoader)
        {
            // RegionGate.Update sets this after requesting a region switch. For an internal gate
            // no loader exists, so complete the vanilla callback against the already-loaded room.
            self.NewWorldLoaded(self.room);
        }
    }

    /// <summary>
    /// A real gate must have a vanilla gateIndex, either authored with GATE or auto-promoted
    /// from locks.txt. It becomes an internal gate only when two distinct connections resolve
    /// to rooms in this same loaded World.
    /// </summary>
    internal static bool IsInternalGate(AbstractRoom room)
    {
        return room != null && room.gateIndex >= 0 && HasTwoResolvedInternalSides(room);
    }

    private static bool HasTwoResolvedInternalSides(AbstractRoom gateRoom)
    {
        if (gateRoom?.world == null || gateRoom.connections == null)
        {
            return false;
        }

        int first = -1;
        for (int i = 0; i < gateRoom.connections.Length; i++)
        {
            int connection = gateRoom.connections[i];
            if (connection < 0 || connection == gateRoom.index || connection == first)
            {
                continue;
            }

            AbstractRoom target;
            try
            {
                target = gateRoom.world.GetAbstractRoom(connection);
            }
            catch
            {
                target = null;
            }

            if (target == null || !ReferenceEquals(target.world, gateRoom.world))
            {
                continue;
            }

            if (first < 0)
            {
                first = connection;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCurrentRoomName(WorldLoader loader, out string roomName)
    {
        roomName = null;
        if (loader?.lines == null || loader.cntr < 0 || loader.cntr >= loader.lines.Count)
        {
            return false;
        }

        string line = loader.lines[loader.cntr];
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int separator = line.IndexOf(" : ", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        roomName = line.Substring(0, separator).Trim();
        return roomName.Length > 0;
    }

    private static void ReloadGateDeclarations()
    {
        DeclaredGateRooms.Clear();

        try
        {
            string path = AssetManager.ResolveFilePath(LocksRelativePath);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Plugin.Logger?.LogWarning(
                    $"InternalGate: could not resolve '{LocksRelativePath}'. Tagless gates cannot be auto-registered.");
                return;
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripComment(lines[i]).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] fields = line.Split(new[] { " : " }, StringSplitOptions.None);
                if (fields.Length < 3)
                {
                    continue;
                }

                string roomName = fields[0].Trim();
                if (roomName.Length > 0)
                {
                    DeclaredGateRooms.Add(roomName);
                }
            }
        }
        catch (Exception ex)
        {
            DeclaredGateRooms.Clear();
            Plugin.Logger?.LogError($"InternalGate: failed to read locks.txt gate declarations: {ex}");
        }
    }

    private static string StripComment(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int hash = text.IndexOf('#');
        int slash = text.IndexOf("//", StringComparison.Ordinal);
        int cut = -1;

        if (hash >= 0)
        {
            cut = hash;
        }
        if (slash >= 0 && (cut < 0 || slash < cut))
        {
            cut = slash;
        }

        return cut >= 0 ? text.Substring(0, cut) : text;
    }

    private static void MarkGatePassed(RegionGate gate)
    {
        try
        {
            AbstractRoom room = gate?.room?.abstractRoom;
            bool[] passed = gate?.room?.world?.regionState?.gatesPassedThrough;
            if (room == null || passed == null || room.gateIndex < 0 || room.gateIndex >= passed.Length)
            {
                return;
            }

            passed[room.gateIndex] = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"InternalGate: could not persist gate-used state: {ex.Message}");
        }
    }
}
