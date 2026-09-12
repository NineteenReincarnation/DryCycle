using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Authoritative editable store for explicit endpoint-to-endpoint world connections.
///
/// Vanilla world.txt only records the destination room for each source exit. When two
/// rooms appear more than once in each other's connection arrays, AbstractRoom.ExitIndex
/// resolves the target with Array.IndexOf and therefore always chooses the first pipe.
/// WorldTopology.json stores the missing target-node identity and direction without
/// replacing or rewriting unrelated world.txt syntax.
/// </summary>
internal static class WorldTopologyRegistry
{
    internal const string FileName = "WorldTopology.json";

    private const int CurrentVersion = 1;
    private const int MaxAssemblyParentSearchDepth = 8;

    private static WorldTopologyDocument document = new();
    private static readonly List<string> warnings = new();
    private static bool loaded;

    internal static bool Dirty { get; private set; }
    internal static string LoadedPath { get; private set; } = string.Empty;
    internal static string LoadError { get; private set; }
    internal static IReadOnlyList<string> Warnings => warnings;

    internal static void EnsureLoaded()
    {
        if (!loaded) Reload();
    }

    internal static void Reload()
    {
        if (Dirty) return;

        loaded = true;
        warnings.Clear();
        LoadError = null;
        LoadedPath = ResolvePath(forSave: true) ?? string.Empty;

        if (LoadedPath.Length == 0 || !File.Exists(LoadedPath))
        {
            document = new WorldTopologyDocument();
            Dirty = false;
            return;
        }

        if (TryLoadFile(LoadedPath, out WorldTopologyDocument parsed, out string error))
        {
            document = parsed;
            Dirty = false;
            return;
        }

        string backup = LoadedPath + ".bak";
        string backupError = null;
        if (File.Exists(backup) && TryLoadFile(backup, out parsed, out backupError))
        {
            document = parsed;
            Dirty = true;
            LoadError = $"Primary {FileName} is invalid ({error}); recovered from {Path.GetFileName(backup)}.";
            global::DryCycle.Plugin.Logger?.LogWarning("WorldTopology: " + LoadError);
            return;
        }

        document = new WorldTopologyDocument();
        Dirty = false;
        LoadError = File.Exists(backup)
            ? $"{FileName} and backup are invalid. Primary: {error}; backup: {backupError}"
            : $"{FileName} is invalid and has no backup: {error}";
        global::DryCycle.Plugin.Logger?.LogError("WorldTopology: " + LoadError);
    }

    internal static WorldConnectionEdge[] GetRegionEdges(string region)
    {
        EnsureLoaded();
        if (!document.TryGetRegion(region, out List<WorldConnectionEdge> edges) || edges.Count == 0)
            return Array.Empty<WorldConnectionEdge>();

        WorldConnectionEdge[] result = new WorldConnectionEdge[edges.Count];
        for (int i = 0; i < edges.Count; i++) result[i] = edges[i].Clone();
        return result;
    }

    internal static bool TryAddEdge(
        string region,
        WorldConnectionEndpoint a,
        WorldConnectionEndpoint b,
        WorldConnectionDirection direction,
        out string edgeId,
        out string error)
    {
        EnsureLoaded();
        edgeId = string.Empty;
        error = null;

        if (!a.IsValid || !b.IsValid)
        {
            error = "Both connection endpoints must have a room and a non-negative node index.";
            return false;
        }
        if (a.Equals(b))
        {
            error = "A connection cannot connect an endpoint to itself.";
            return false;
        }

        List<WorldConnectionEdge> edges = document.GetOrCreateRegion(region);
        for (int i = 0; i < edges.Count; i++)
        {
            WorldConnectionEdge edge = edges[i];
            if (SameUndirectedEdge(edge.A, edge.B, a, b))
            {
                error = "That endpoint pair is already connected.";
                return false;
            }
            if (edge.A.Equals(a) || edge.B.Equals(a))
            {
                error = a + " is already used by connection " + edge.Id + ".";
                return false;
            }
            if (edge.A.Equals(b) || edge.B.Equals(b))
            {
                error = b + " is already used by connection " + edge.Id + ".";
                return false;
            }
        }

        edgeId = Guid.NewGuid().ToString("N");
        edges.Add(new WorldConnectionEdge
        {
            Id = edgeId,
            A = a,
            B = b,
            Direction = direction
        });
        Dirty = true;
        return true;
    }

    internal static bool RemoveEdge(string region, string edgeId)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(edgeId) ||
            !document.TryGetRegion(region, out List<WorldConnectionEdge> edges))
            return false;

        for (int i = 0; i < edges.Count; i++)
        {
            if (!string.Equals(edges[i].Id, edgeId, StringComparison.OrdinalIgnoreCase)) continue;
            edges.RemoveAt(i);
            Dirty = true;
            return true;
        }
        return false;
    }

    internal static bool SetDirection(
        string region,
        string edgeId,
        WorldConnectionDirection direction)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(edgeId) ||
            !document.TryGetRegion(region, out List<WorldConnectionEdge> edges))
            return false;

        for (int i = 0; i < edges.Count; i++)
        {
            if (!string.Equals(edges[i].Id, edgeId, StringComparison.OrdinalIgnoreCase)) continue;
            if (edges[i].Direction == direction) return false;
            edges[i].Direction = direction;
            Dirty = true;
            return true;
        }
        return false;
    }

    internal static bool TryResolveOutgoing(
        string region,
        string sourceRoom,
        int sourceNode,
        out WorldConnectionEndpoint destination,
        out string edgeId)
    {
        EnsureLoaded();
        destination = default;
        edgeId = string.Empty;
        if (!document.TryGetRegion(region, out List<WorldConnectionEdge> edges)) return false;

        WorldConnectionEndpoint source = new(sourceRoom, sourceNode);
        for (int i = 0; i < edges.Count; i++)
        {
            WorldConnectionEdge edge = edges[i];
            if (edge.A.Equals(source) && edge.AllowsFromA)
            {
                destination = edge.B;
                edgeId = edge.Id;
                return true;
            }
            if (edge.B.Equals(source) && edge.AllowsFromB)
            {
                destination = edge.A;
                edgeId = edge.Id;
                return true;
            }
        }
        return false;
    }

    internal static List<WorldTopologyIssue> ValidateRegion(
        string region,
        Func<string, bool> roomExists = null,
        Func<string, int, bool> nodeExists = null)
    {
        EnsureLoaded();
        List<WorldTopologyIssue> issues = new();
        if (!document.TryGetRegion(region, out List<WorldConnectionEdge> edges)) return issues;

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<WorldConnectionEndpoint, string> endpointOwners = new();

        for (int i = 0; i < edges.Count; i++)
        {
            WorldConnectionEdge edge = edges[i];
            if (string.IsNullOrWhiteSpace(edge.Id) || !ids.Add(edge.Id))
                AddIssue(issues, WorldTopologyIssueKind.DuplicateId, edge.Id, "Connection id is empty or duplicated.");

            if (!edge.A.IsValid || !edge.B.IsValid || edge.A.Equals(edge.B))
                AddIssue(issues, WorldTopologyIssueKind.InvalidEndpoint, edge.Id, "Connection contains an invalid endpoint.");

            RegisterEndpoint(issues, endpointOwners, edge.Id, edge.A);
            RegisterEndpoint(issues, endpointOwners, edge.Id, edge.B);

            for (int j = 0; j < i; j++)
            {
                if (SameUndirectedEdge(edges[j].A, edges[j].B, edge.A, edge.B))
                {
                    AddIssue(issues, WorldTopologyIssueKind.DuplicateEdge, edge.Id, "The same endpoint pair is connected more than once.");
                    break;
                }
            }

            ValidateEndpointAgainstWorld(issues, edge.Id, edge.A, roomExists, nodeExists);
            ValidateEndpointAgainstWorld(issues, edge.Id, edge.B, roomExists, nodeExists);
        }

        return issues;
    }

    internal static bool Save()
    {
        EnsureLoaded();
        string path = LoadedPath.Length > 0 ? LoadedPath : ResolvePath(forSave: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            global::DryCycle.Plugin.Logger?.LogError("WorldTopology: could not resolve a writable world/" + FileName + " path.");
            return false;
        }

        string temp = path + ".tmp";
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(temp, Json.Serialize(BuildJsonRoot()));
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
                File.Delete(path);
            }
            File.Move(temp, path);

            LoadedPath = path;
            Dirty = false;
            LoadError = null;
            warnings.Clear();
            return true;
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogError("WorldTopology save failed: " + ex);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    private static bool TryLoadFile(string path, out WorldTopologyDocument parsed, out string error)
    {
        parsed = new WorldTopologyDocument();
        error = null;
        try
        {
            object value = Json.Deserialize(File.ReadAllText(path));
            if (value is not Dictionary<string, object> root)
            {
                error = "root JSON value is not an object";
                return false;
            }

            if (root.TryGetValue("version", out object versionObject) &&
                TryInt(versionObject, out int version) && version != CurrentVersion)
            {
                warnings.Add($"{FileName} version {version} differs from supported version {CurrentVersion}; known fields were loaded.");
            }

            if (!root.TryGetValue("regions", out object regionsObject) || regionsObject == null) return true;
            if (regionsObject is not Dictionary<string, object> regions)
            {
                error = "'regions' is not an object";
                return false;
            }

            foreach (KeyValuePair<string, object> regionPair in regions)
            {
                string region = WorldTopologyDocument.NormalizeRegion(regionPair.Key);
                if (region.Length == 0 || regionPair.Value is not Dictionary<string, object> regionObject)
                {
                    warnings.Add("Skipped malformed region entry '" + regionPair.Key + "'.");
                    continue;
                }
                if (!regionObject.TryGetValue("connections", out object connectionsObject) || connectionsObject == null)
                    continue;
                if (connectionsObject is not List<object> connections)
                {
                    warnings.Add(region + ": 'connections' is not an array and was ignored.");
                    continue;
                }

                List<WorldConnectionEdge> target = parsed.GetOrCreateRegion(region);
                for (int i = 0; i < connections.Count; i++)
                {
                    if (connections[i] is not Dictionary<string, object> connectionObject)
                    {
                        warnings.Add(region + ": skipped connection #" + i + " (entry is not an object)." );
                        continue;
                    }
                    if (!TryParseEdge(connectionObject, out WorldConnectionEdge edge, out string edgeError))
                    {
                        warnings.Add(region + ": skipped connection #" + i + " (" + edgeError + ").");
                        continue;
                    }
                    target.Add(edge);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseEdge(
        Dictionary<string, object> value,
        out WorldConnectionEdge edge,
        out string error)
    {
        edge = null;
        error = null;
        if (!value.TryGetValue("a", out object aObject) ||
            !value.TryGetValue("b", out object bObject) ||
            !TryParseEndpoint(aObject, out WorldConnectionEndpoint a) ||
            !TryParseEndpoint(bObject, out WorldConnectionEndpoint b))
        {
            error = "invalid endpoint";
            return false;
        }

        string id = value.TryGetValue("id", out object idObject)
            ? Convert.ToString(idObject, CultureInfo.InvariantCulture)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");

        WorldConnectionDirection direction = WorldConnectionDirection.Bidirectional;
        if (value.TryGetValue("direction", out object directionObject) && directionObject != null)
            direction = ParseDirection(Convert.ToString(directionObject, CultureInfo.InvariantCulture));

        edge = new WorldConnectionEdge
        {
            Id = id.Trim(),
            A = a,
            B = b,
            Direction = direction
        };
        return true;
    }

    private static bool TryParseEndpoint(object value, out WorldConnectionEndpoint endpoint)
    {
        endpoint = default;
        if (value is not Dictionary<string, object> obj ||
            !obj.TryGetValue("room", out object roomObject) ||
            !obj.TryGetValue("node", out object nodeObject) ||
            !TryInt(nodeObject, out int node))
            return false;

        endpoint = new WorldConnectionEndpoint(
            Convert.ToString(roomObject, CultureInfo.InvariantCulture),
            node);
        return endpoint.IsValid;
    }

    private static Dictionary<string, object> BuildJsonRoot()
    {
        Dictionary<string, object> root = new()
        {
            ["version"] = CurrentVersion
        };
        Dictionary<string, object> regionsObject = new(StringComparer.OrdinalIgnoreCase);
        List<string> regionNames = new(document.Regions.Keys);
        regionNames.Sort(StringComparer.OrdinalIgnoreCase);

        for (int regionIndex = 0; regionIndex < regionNames.Count; regionIndex++)
        {
            string region = regionNames[regionIndex];
            List<WorldConnectionEdge> source = document.Regions[region];
            List<object> connections = new();
            for (int i = 0; i < source.Count; i++)
            {
                WorldConnectionEdge edge = source[i];
                connections.Add(new Dictionary<string, object>
                {
                    ["id"] = edge.Id,
                    ["direction"] = DirectionName(edge.Direction),
                    ["a"] = EndpointJson(edge.A),
                    ["b"] = EndpointJson(edge.B)
                });
            }

            regionsObject[region] = new Dictionary<string, object>
            {
                ["connections"] = connections
            };
        }

        root["regions"] = regionsObject;
        return root;
    }

    private static Dictionary<string, object> EndpointJson(WorldConnectionEndpoint endpoint) => new()
    {
        ["room"] = endpoint.Room,
        ["node"] = endpoint.NodeIndex
    };

    private static string DirectionName(WorldConnectionDirection direction) => direction switch
    {
        WorldConnectionDirection.AToB => "AToB",
        WorldConnectionDirection.BToA => "BToA",
        _ => "Bidirectional"
    };

    private static WorldConnectionDirection ParseDirection(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Equals("AToB", StringComparison.OrdinalIgnoreCase) || normalized == "→")
            return WorldConnectionDirection.AToB;
        if (normalized.Equals("BToA", StringComparison.OrdinalIgnoreCase) || normalized == "←")
            return WorldConnectionDirection.BToA;
        return WorldConnectionDirection.Bidirectional;
    }

    private static void RegisterEndpoint(
        List<WorldTopologyIssue> issues,
        Dictionary<WorldConnectionEndpoint, string> owners,
        string edgeId,
        WorldConnectionEndpoint endpoint)
    {
        if (!endpoint.IsValid) return;
        if (owners.TryGetValue(endpoint, out string owner) &&
            !string.Equals(owner, edgeId, StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(
                issues,
                WorldTopologyIssueKind.EndpointConflict,
                edgeId,
                endpoint + " is already owned by connection " + owner + ".");
        }
        else
        {
            owners[endpoint] = edgeId;
        }
    }

    private static void ValidateEndpointAgainstWorld(
        List<WorldTopologyIssue> issues,
        string edgeId,
        WorldConnectionEndpoint endpoint,
        Func<string, bool> roomExists,
        Func<string, int, bool> nodeExists)
    {
        if (!endpoint.IsValid) return;
        if (roomExists != null && !roomExists(endpoint.Room))
        {
            AddIssue(issues, WorldTopologyIssueKind.MissingRoom, edgeId, "Room does not exist: " + endpoint.Room);
            return;
        }
        if (nodeExists != null && !nodeExists(endpoint.Room, endpoint.NodeIndex))
            AddIssue(issues, WorldTopologyIssueKind.MissingNode, edgeId, "Exit does not exist: " + endpoint);
    }

    private static void AddIssue(
        List<WorldTopologyIssue> issues,
        WorldTopologyIssueKind kind,
        string edgeId,
        string message)
    {
        issues.Add(new WorldTopologyIssue
        {
            Kind = kind,
            EdgeId = edgeId ?? string.Empty,
            Message = message ?? string.Empty
        });
    }

    private static bool SameUndirectedEdge(
        WorldConnectionEndpoint a1,
        WorldConnectionEndpoint b1,
        WorldConnectionEndpoint a2,
        WorldConnectionEndpoint b2) =>
        (a1.Equals(a2) && b1.Equals(b2)) ||
        (a1.Equals(b2) && b1.Equals(a2));

    private static bool TryInt(object value, out int number)
    {
        number = 0;
        try
        {
            double parsed = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(parsed) || double.IsInfinity(parsed)) return false;
            number = (int)parsed;
            return Math.Abs(parsed - number) < 0.0001d;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePath(bool forSave)
    {
        string assemblyOwned = ResolvePathFromContainingMod();
        if (!string.IsNullOrWhiteSpace(assemblyOwned) && (forSave || File.Exists(assemblyOwned)))
            return assemblyOwned;

        try
        {
            if (ModManager.ActiveMods != null)
            {
                for (int i = 0; i < ModManager.ActiveMods.Count; i++)
                {
                    ModManager.Mod mod = ModManager.ActiveMods[i];
                    if (mod == null ||
                        !string.Equals(mod.id, global::DryCycle.Plugin.RainWorldModId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string[] roots = { mod.path, mod.NewestPath, mod.TargetedPath, mod.basePath };
                    string first = null;
                    for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                    {
                        if (string.IsNullOrWhiteSpace(roots[rootIndex])) continue;
                        string candidate = Path.Combine(roots[rootIndex], "world", FileName);
                        first ??= candidate;
                        if (File.Exists(candidate)) return candidate;
                    }
                    if (forSave && first != null) return first;
                }
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning("WorldTopology path lookup failed: " + ex.Message);
        }

        string resolved = AssetManager.ResolveFilePath("world/" + FileName);
        return forSave || File.Exists(resolved) ? resolved : null;
    }

    private static string ResolvePathFromContainingMod()
    {
        try
        {
            string assemblyPath = typeof(global::DryCycle.Plugin).Assembly.Location;
            string directoryPath = string.IsNullOrWhiteSpace(assemblyPath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
            if (string.IsNullOrWhiteSpace(directoryPath)) return null;

            DirectoryInfo directory = new(directoryPath);
            for (int depth = 0;
                 directory != null && depth < MaxAssemblyParentSearchDepth;
                 depth++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "modinfo.json")))
                    return Path.Combine(directory.FullName, "world", FileName);
            }
        }
        catch (Exception ex)
        {
            global::DryCycle.Plugin.Logger?.LogWarning("WorldTopology assembly path lookup failed: " + ex.Message);
        }
        return null;
    }
}
