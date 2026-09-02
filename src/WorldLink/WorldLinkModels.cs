using System;
using System.Globalization;
using UnityEngine;

namespace DryCycle.WorldLink;

internal enum WorldLinkTransitMode
{
    VanillaNode = 0,
    DirectTransit = 1,
    CrossRegion = 2
}

internal readonly struct WorldLinkPortAddress : IEquatable<WorldLinkPortAddress>
{
    internal readonly string Room;
    internal readonly string Gate;
    internal readonly string Port;

    internal WorldLinkPortAddress(string room, string gate, string port)
    {
        Room = Normalize(room);
        Gate = Normalize(gate);
        Port = Normalize(port);
    }

    internal bool IsValid => Room.Length > 0 && Gate.Length > 0 && Port.Length > 0;
    internal string SaveKey => $"DRYCYCLE_WORLDLINK|{Room}|{Gate}|{Port}";

    public bool Equals(WorldLinkPortAddress other) =>
        string.Equals(Room, other.Room, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Gate, other.Gate, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Port, other.Port, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object obj) => obj is WorldLinkPortAddress other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Room ?? string.Empty);
            hash = hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Gate ?? string.Empty);
            hash = hash * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Port ?? string.Empty);
            return hash;
        }
    }

    public override string ToString() => $"{Room}/{Gate}/{Port}";

    internal static bool TryParse(string text, out WorldLinkPortAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split('/');
        if (parts.Length != 3)
        {
            return false;
        }

        address = new WorldLinkPortAddress(parts[0], parts[1], parts[2]);
        return address.IsValid;
    }

    internal static string Normalize(string value) => (value ?? string.Empty).Trim();
}

internal sealed class MultiGateControllerData : PlacedObject.Data
{
    private const string Version = "V1";
    public string GateId = "MainGate";
    public Vector2 PanelPos = new(40f, 80f);

    internal MultiGateControllerData(PlacedObject owner) : base(owner) { }

    public override void FromString(string s)
    {
        try
        {
            string[] p = (s ?? string.Empty).Split('~');
            if (p.Length >= 4 && p[0] == Version)
            {
                GateId = SafeId(p[1], "MainGate");
                PanelPos = new Vector2(ParseFloat(p[2], PanelPos.x), ParseFloat(p[3], PanelPos.y));
                unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(p, 4);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"WorldLink controller data parse failed: {ex.Message}");
        }
    }

    public override string ToString()
    {
        string result = string.Join("~", new[]
        {
            Version,
            GateId,
            F(PanelPos.x),
            F(PanelPos.y)
        });
        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    internal static string SafeId(string value, string fallback)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return fallback;
        char[] buffer = new char[trimmed.Length];
        int length = 0;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') buffer[length++] = c;
        }
        return length == 0 ? fallback : new string(buffer, 0, length);
    }

    internal static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && !float.IsNaN(value) && !float.IsInfinity(value)
            ? value
            : fallback;

    internal static int ParseInt(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    internal static bool ParseBool(string s, bool fallback) =>
        bool.TryParse(s, out bool value) ? value : fallback;

    internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

internal sealed class MultiGatePortData : PlacedObject.Data
{
    private const string VersionV3 = "V3";
    private const string VersionV2 = "V2";
    private const int V3FieldCount = 28;
    private const int V2FieldCount = 27;

    public string GateId = "MainGate";
    public string PortId = "PortA";
    public Vector2 Direction = Vector2.right;
    public float PassageWidth = 180f;
    public float PanelThickness = 12f;
    public float TriggerDepth = 120f;
    public float OpenFrames = 95f;
    public float CloseFrames = 120f;
    public int VanillaNodeIndex = -1;
    public WorldLinkTransitMode TransitMode = WorldLinkTransitMode.VanillaNode;
    public string DestinationRegion = string.Empty;
    public string DestinationRoom = string.Empty;
    public string DestinationGateId = string.Empty;
    public string DestinationPortId = string.Empty;
    public Vector2 GlyphOffset = new(-32f, 0f);
    public Vector2 MapAnchorOffset = Vector2.zero;
    public Vector2 MapDirection = Vector2.right;
    public bool MapDirectionOverride;
    public Vector2 MapGlyphOffset = new(-16f, 12f);
    public bool HideExternalDestinationUntilTraversed = true;
    public bool Enabled = true;
    public Vector2 PanelPos = new(110f, 80f);

    internal MultiGatePortData(PlacedObject owner) : base(owner) { }

    internal Vector2 Normal
    {
        get
        {
            Vector2 d = Direction;
            if (d.sqrMagnitude < 0.0001f)
            {
                d = Vector2.right;
            }
            return d.normalized;
        }
    }

    internal Vector2 Tangent
    {
        get
        {
            Vector2 n = Normal;
            return new Vector2(-n.y, n.x);
        }
    }

    internal Vector2 EffectiveMapDirection => MapDirectionOverride ? SafeDirection(MapDirection) : Normal;

    internal WorldLinkPortAddress Address(string roomName) => new(roomName, GateId, PortId);

    internal WorldLinkPortAddress DestinationAddress =>
        new(DestinationRoom, DestinationGateId, DestinationPortId);

    public override void FromString(string s)
    {
        try
        {
            string[] p = (s ?? string.Empty).Split('~');
            bool v3 = p.Length >= V3FieldCount && p[0] == VersionV3;
            bool v2 = p.Length >= V2FieldCount && p[0] == VersionV2;
            if (!v3 && !v2)
            {
                return;
            }

            GateId = MultiGateControllerData.SafeId(p[1], "MainGate");
            PortId = MultiGateControllerData.SafeId(p[2], "PortA");
            Direction = SafeDirection(new Vector2(PF(p[3], 1f), PF(p[4], 0f)));
            PassageWidth = Mathf.Clamp(PF(p[5], PassageWidth), 40f, 900f);
            PanelThickness = Mathf.Clamp(PF(p[6], PanelThickness), 2f, 60f);
            TriggerDepth = Mathf.Clamp(PF(p[7], TriggerDepth), 30f, 600f);
            OpenFrames = Mathf.Clamp(PF(p[8], OpenFrames), 15f, 600f);
            CloseFrames = Mathf.Clamp(PF(p[9], CloseFrames), 15f, 600f);
            VanillaNodeIndex = MultiGateControllerData.ParseInt(p[10], -1);
            TransitMode = Enum.TryParse(p[11], true, out WorldLinkTransitMode mode) ? mode : WorldLinkTransitMode.VanillaNode;
            DestinationRegion = Clean(p[12]);
            DestinationRoom = Clean(p[13]);
            DestinationGateId = MultiGateControllerData.SafeId(p[14], string.Empty);
            DestinationPortId = MultiGateControllerData.SafeId(p[15], string.Empty);
            GlyphOffset = new Vector2(PF(p[16], GlyphOffset.x), PF(p[17], GlyphOffset.y));
            MapAnchorOffset = new Vector2(PF(p[18], 0f), PF(p[19], 0f));
            MapDirection = SafeDirection(new Vector2(PF(p[20], Direction.x), PF(p[21], Direction.y)));
            if (v3)
            {
                MapDirectionOverride = MultiGateControllerData.ParseBool(p[22], false);
                MapGlyphOffset = new Vector2(PF(p[23], MapGlyphOffset.x), PF(p[24], MapGlyphOffset.y));
                HideExternalDestinationUntilTraversed = MultiGateControllerData.ParseBool(p[25], true);
                Enabled = MultiGateControllerData.ParseBool(p[26], true);
                PanelPos = ParseVector(p[27], PanelPos);
                unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(p, V3FieldCount);
            }
            else
            {
                // V2 always serialized a concrete map vector. Preserve it as a manual
                // override when reading old development rooms.
                MapDirectionOverride = true;
                MapGlyphOffset = new Vector2(PF(p[22], MapGlyphOffset.x), PF(p[23], MapGlyphOffset.y));
                HideExternalDestinationUntilTraversed = MultiGateControllerData.ParseBool(p[24], true);
                Enabled = MultiGateControllerData.ParseBool(p[25], true);
                PanelPos = ParseVector(p[26], PanelPos);
                unrecognizedAttributes = SaveUtils.PopulateUnrecognizedStringAttrs(p, V2FieldCount);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"WorldLink port data parse failed: {ex.Message}");
        }
    }

    public override string ToString()
    {
        string result = string.Join("~", new[]
        {
            VersionV3,
            GateId,
            PortId,
            FF(Direction.x), FF(Direction.y),
            FF(PassageWidth), FF(PanelThickness), FF(TriggerDepth), FF(OpenFrames), FF(CloseFrames),
            VanillaNodeIndex.ToString(CultureInfo.InvariantCulture),
            TransitMode.ToString(),
            DestinationRegion, DestinationRoom, DestinationGateId, DestinationPortId,
            FF(GlyphOffset.x), FF(GlyphOffset.y),
            FF(MapAnchorOffset.x), FF(MapAnchorOffset.y),
            FF(MapDirection.x), FF(MapDirection.y),
            MapDirectionOverride.ToString(),
            FF(MapGlyphOffset.x), FF(MapGlyphOffset.y),
            HideExternalDestinationUntilTraversed.ToString(),
            Enabled.ToString(),
            FF(PanelPos.x) + "," + FF(PanelPos.y)
        });
        result = SaveState.SetCustomData(this, result);
        return SaveUtils.AppendUnrecognizedStringAttrs(result, "~", unrecognizedAttributes);
    }

    private static Vector2 ParseVector(string text, Vector2 fallback)
    {
        string[] parts = (text ?? string.Empty).Split(',');
        return parts.Length == 2 ? new Vector2(PF(parts[0], fallback.x), PF(parts[1], fallback.y)) : fallback;
    }

    private static Vector2 SafeDirection(Vector2 v) => v.sqrMagnitude < 0.0001f ? Vector2.right : v.normalized;
    private static string Clean(string s) => (s ?? string.Empty).Trim().Replace("~", "_");
    private static float PF(string s, float fallback) => MultiGateControllerData.ParseFloat(s, fallback);
    private static string FF(float f) => MultiGateControllerData.F(f);
}
