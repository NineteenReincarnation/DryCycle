using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HUD;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal static class WorldLinkMapRuntime
{
    private sealed class PortInfo
    {
        internal WorldLinkPortAddress Address;
        internal int RoomIndex;
        internal string RoomName;
        internal Vector2 PhysicalPosInRoom;
        internal Vector2 MapPosInRoom;
        internal Vector2 MapDirection;
        internal Vector2 GlyphPosInRoom;
        internal WorldLinkTransitMode Mode;
        internal int NodeIndex;
        internal string DestinationRoom;
        internal string DestinationRegion;
        internal bool HideExternalDestination;
    }

    private sealed class ConnectionInfo
    {
        internal PortInfo A;
        internal PortInfo B;
    }

    private sealed class MapState
    {
        internal bool Initialized;
        internal readonly List<PortInfo> Ports = new();
        internal readonly Dictionary<Map.OnMapConnection, ConnectionInfo> Connections = new();
    }

    private static readonly ConditionalWeakTable<Map, MapState> States = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        On.HUD.Map.Update += MapUpdate;
        On.HUD.Map.OnMapConnection.DrawSprites += DrawConnection;
    }

    internal static void Disable()
    {
        if (!_enabled) return;
        _enabled = false;
        On.HUD.Map.Update -= MapUpdate;
        On.HUD.Map.OnMapConnection.DrawSprites -= DrawConnection;
    }

    private static void MapUpdate(On.HUD.Map.orig_Update orig, Map self)
    {
        orig(self);
        if (self?.mapData == null || self.mapObjects == null || self.notRevealedFadeMarkers == null) return;
        MapState state = States.GetOrCreateValue(self);
        if (!state.Initialized)
        {
            Initialize(self, state);
        }
    }

    private static void Initialize(Map map, MapState state)
    {
        state.Initialized = true;
        if (map.mapData.type == Map.MapType.WarpLinks) return;

        SlugcatStats.Timeline timeline = map.GetSaveState()?.currentTimelinePosition;
        for (int i = 0; i < map.mapData.roomNames.Length; i++)
        {
            string roomName = map.mapData.roomNames[i];
            if (string.IsNullOrWhiteSpace(roomName)) continue;
            int roomIndex = map.mapData.firstRoomIndex + i;
            try
            {
                RoomSettings settings = new(roomName, null, template: false, firstTemplate: false, timeline, null);
                for (int j = 0; j < settings.placedObjects.Count; j++)
                {
                    PlacedObject po = settings.placedObjects[j];
                    if (po?.type != WorldLinkPlacedObjects.PortType || !po.active || po.data is not MultiGatePortData pd || !pd.Enabled) continue;
                    PortInfo info = new()
                    {
                        Address = pd.Address(roomName),
                        RoomIndex = roomIndex,
                        RoomName = roomName,
                        PhysicalPosInRoom = po.pos,
                        MapPosInRoom = po.pos + pd.MapAnchorOffset,
                        MapDirection = pd.EffectiveMapDirection,
                        GlyphPosInRoom = po.pos + pd.MapAnchorOffset + pd.MapGlyphOffset,
                        Mode = pd.TransitMode,
                        NodeIndex = pd.VanillaNodeIndex,
                        DestinationRoom = pd.DestinationRoom?.Trim() ?? string.Empty,
                        DestinationRegion = pd.DestinationRegion?.Trim() ?? string.Empty,
                        HideExternalDestination = pd.HideExternalDestinationUntilTraversed
                    };
                    state.Ports.Add(info);
                    AddPortMarker(map, info);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"WorldLink map: could not read RoomSettings for {roomName}: {ex.Message}");
            }
        }

        for (int i = 0; i < map.mapConnections.Count; i++)
        {
            Map.OnMapConnection connection = map.mapConnections[i];
            ConnectionInfo ci = new()
            {
                A = MatchPort(map, state.Ports, connection.roomA, connection.roomB, connection.posInRoomA),
                B = MatchPort(map, state.Ports, connection.roomB, connection.roomA, connection.posInRoomB)
            };
            if (ci.A != null || ci.B != null) state.Connections[connection] = ci;
        }
    }

    private static PortInfo MatchPort(Map map, List<PortInfo> ports, int sourceRoom, int targetRoom, IntVector2 vanillaPos)
    {
        string targetName = map.mapData.NameOfRoom(targetRoom);
        PortInfo nearest = null;
        float nearestDistance = 100f * 100f;
        Vector2 vp = vanillaPos.ToVector2() * 20f;
        for (int i = 0; i < ports.Count; i++)
        {
            PortInfo p = ports[i];
            if (p.RoomIndex != sourceRoom || p.Mode != WorldLinkTransitMode.VanillaNode) continue;
            string configuredOrResolved = ResolveVanillaDestination(map, p);
            if (configuredOrResolved.Length > 0 && string.Equals(configuredOrResolved, targetName, StringComparison.OrdinalIgnoreCase)) return p;
            float d = Vector2.SqrMagnitude(p.PhysicalPosInRoom - vp);
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearest = p;
            }
        }
        return nearest;
    }


    private static string ResolveVanillaDestination(Map map, PortInfo port)
    {
        if (!string.IsNullOrWhiteSpace(port.DestinationRoom)) return port.DestinationRoom;
        if (port.NodeIndex < 0 || map?.hud?.rainWorld?.processManager?.currentMainLoop is not RainWorldGame game) return string.Empty;
        World world = game.overWorld?.activeWorld;
        AbstractRoom room = world?.GetAbstractRoom(port.RoomName);
        if (room?.connections == null || port.NodeIndex >= room.connections.Length) return string.Empty;
        int targetIndex = room.connections[port.NodeIndex];
        return targetIndex >= 0 ? world.GetAbstractRoom(targetIndex)?.name ?? string.Empty : string.Empty;
    }

    private static void AddPortMarker(Map map, PortInfo info)
    {
        Map.FadeInMarker marker = info.Mode == WorldLinkTransitMode.CrossRegion
            ? new ExternalPortMarker(map, info)
            : new PortMarker(map, info);
        map.mapObjects.Add(marker);
        marker.SetInvisible();
        if (map.discoverTexture != null)
        {
            IntVector2 px = IntVector2.FromVector2(map.OnTexturePos(marker.inRoomPos, marker.room, accountForLayer: true) / map.DiscoverResolution);
            if (px.x >= 0 && px.y >= 0 && px.x < ((Texture)map.discoverTexture).width && px.y < ((Texture)map.discoverTexture).height && map.discoverTexture.GetPixel(px.x, px.y).r > 0f)
            {
                marker.FadeIn(0.1f);
                return;
            }
        }
        map.notRevealedFadeMarkers.Add(marker);
    }

    private static void DrawConnection(On.HUD.Map.OnMapConnection.orig_DrawSprites orig, Map.OnMapConnection self, float timeStacker)
    {
        orig(self, timeStacker);
        if (self?.map == null || self.lineSprite is not TriangleMesh mesh || !States.TryGetValue(self.map, out MapState state) || !state.Connections.TryGetValue(self, out ConnectionInfo info)) return;
        if (!self.lineSprite.isVisible) return;

        Vector2 a = info.A != null ? self.map.RoomToMapPos(info.A.MapPosInRoom, self.roomA, timeStacker) : new Vector2(self.dotA.x, self.dotA.y);
        Vector2 b = info.B != null ? self.map.RoomToMapPos(info.B.MapPosInRoom, self.roomB, timeStacker) : new Vector2(self.dotB.x, self.dotB.y);
        Vector2 dirA = info.A != null ? info.A.MapDirection : Custom.fourDirections[self.dirA].ToVector2();
        Vector2 dirB = info.B != null ? info.B.MapDirection : Custom.fourDirections[self.dirB].ToVector2();
        if (dirA.sqrMagnitude < 0.001f) dirA = Vector2.right;
        if (dirB.sqrMagnitude < 0.001f) dirB = Vector2.left;
        dirA.Normalize(); dirB.Normalize();

        self.dotA.x = a.x; self.dotA.y = a.y;
        self.dotB.x = b.x; self.dotB.y = b.y;
        float distance = Vector2.Distance(a, b);
        if (distance > 300f) distance = Mathf.Lerp(distance, 300f, 0.5f);
        float control = distance / 3f;
        Vector2 prev = a;
        for (int i = 0; i < self.segments; i++)
        {
            float t = (i + 1f) / self.segments;
            Vector2 next = Custom.Bezier(a, a - dirA * control, b, b - dirB * control, t);
            Vector2 perpendicular = Custom.PerpendicularVector((prev - next).normalized);
            mesh.MoveVertice(i * 4, Vector2.Lerp(prev, next, 0.5f) - perpendicular);
            mesh.MoveVertice(i * 4 + 1, Vector2.Lerp(prev, next, 0.5f) + perpendicular);
            mesh.MoveVertice(i * 4 + 2, next - perpendicular);
            mesh.MoveVertice(i * 4 + 3, next + perpendicular);
            prev = next;
        }
    }

    private class PortMarker : Map.FadeInMarker
    {
        protected readonly PortInfo Info;
        private readonly FSprite _routeArrow;
        internal PortMarker(Map map, PortInfo info) : base(map, info.RoomIndex, info.GlyphPosInRoom, 3f)
        {
            Info = info;
            symbolSprite = WorldLinkGlyphs.Create(info.Address);
            map.inFrontContainer.AddChild(symbolSprite);
            symbolSprite.isVisible = false;
            _routeArrow = new FSprite("ShortcutArrow") { scale = 0.6f };
            map.inFrontContainer.AddChild(_routeArrow);
            _routeArrow.isVisible = false;
        }

        public override void Draw(float timeStacker)
        {
            base.Draw(timeStacker);
            bkgFade.isVisible = map.visible;
            WorldLinkGlyphs.Refresh(symbolSprite, Info.Address);
            symbolSprite.isVisible = map.visible;
            _routeArrow.isVisible = map.visible;
            if (!map.visible) return;
            float alpha = Mathf.Lerp(map.lastFade, map.fade, timeStacker) * Mathf.Lerp(lastFade, fade, timeStacker);
            Vector2 pos = map.RoomToMapPos(inRoomPos, room, timeStacker);
            bkgFade.x = pos.x; bkgFade.y = pos.y; bkgFade.alpha = alpha * 0.45f; bkgFade.scale = 10f;
            symbolSprite.x = pos.x; symbolSprite.y = pos.y; symbolSprite.alpha = alpha;
            Vector2 mapAnchor = map.RoomToMapPos(Info.MapPosInRoom, room, timeStacker);
            Vector2 routeDir = Info.MapDirection.sqrMagnitude < 0.001f ? Vector2.right : Info.MapDirection.normalized;
            _routeArrow.x = mapAnchor.x + routeDir.x * 9f;
            _routeArrow.y = mapAnchor.y + routeDir.y * 9f;
            _routeArrow.rotation = Custom.VecToDeg(routeDir);
            _routeArrow.alpha = alpha;
            bool open = CanUseOnMap(map, Info.Address);
            symbolSprite.color = Color.Lerp(open ? global::Menu.Menu.MenuRGB(global::Menu.Menu.MenuColors.DarkGrey) : Color.red,
                global::Menu.Menu.MenuRGB(global::Menu.Menu.MenuColors.White), 0.5f + 0.5f * Mathf.Sin((map.counter + timeStacker) / 14f));
        }

        public override void Destroy()
        {
            base.Destroy();
            _routeArrow.RemoveFromContainer();
        }

        private static bool CanUseOnMap(Map map, WorldLinkPortAddress address)
        {
            RainWorldGame game = map?.hud?.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
            return GateUnlockRequirements.MeetsForMap(game, address.Room, address, map?.mapData?.currentKarma ?? 0);
        }
    }

    private sealed class ExternalPortMarker : PortMarker
    {
        private readonly TriangleMesh _line;
        private readonly FSprite _arrow;
        private readonly FLabel _label;

        internal ExternalPortMarker(Map map, PortInfo info) : base(map, info)
        {
            _line = TriangleMesh.MakeLongMesh(8, pointyTip: false, customColor: false);
            _line.shader = map.hud.rainWorld.Shaders["MapShortcut"];
            map.inFrontContainer.AddChild(_line);
            _arrow = new FSprite("deerEyeB");
            map.inFrontContainer.AddChild(_arrow);
            _label = new FLabel(Custom.GetFont(), DestinationText());
            map.inFrontContainer.AddChild(_label);
        }

        public override void Draw(float timeStacker)
        {
            base.Draw(timeStacker);
            float alpha = Mathf.Lerp(map.lastFade, map.fade, timeStacker) * Mathf.Lerp(lastFade, fade, timeStacker);
            bool visible = map.visible && alpha > 0f;
            _line.isVisible = visible; _arrow.isVisible = visible; _label.isVisible = visible;
            if (!visible) return;
            Vector2 start = map.RoomToMapPos(Info.MapPosInRoom, room, timeStacker);
            Vector2 dir = Info.MapDirection.sqrMagnitude < 0.001f ? Vector2.right : Info.MapDirection.normalized;
            Vector2 end = start + dir * 58f;
            Vector2 prev = start;
            for (int i = 0; i < 8; i++)
            {
                float t = (i + 1f) / 8f;
                Vector2 next = Vector2.Lerp(start, end, t);
                Vector2 p = Custom.PerpendicularVector((prev - next).normalized);
                _line.MoveVertice(i * 4, Vector2.Lerp(prev, next, 0.5f) - p);
                _line.MoveVertice(i * 4 + 1, Vector2.Lerp(prev, next, 0.5f) + p);
                _line.MoveVertice(i * 4 + 2, next - p);
                _line.MoveVertice(i * 4 + 3, next + p);
                prev = next;
            }
            _line.alpha = alpha;
            _arrow.x = end.x; _arrow.y = end.y; _arrow.alpha = alpha; _arrow.scale = 0.65f;
            _label.x = end.x + dir.x * 12f; _label.y = end.y + dir.y * 12f; _label.alpha = alpha;
            _label.text = DestinationText();
        }

        public override void Destroy()
        {
            base.Destroy();
            _line.RemoveFromContainer(); _arrow.RemoveFromContainer(); _label.RemoveFromContainer();
        }

        private string DestinationText()
        {
            RainWorldGame game = map?.hud?.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
            if (Info.HideExternalDestination &&
                !WorldLinkTraversal.HasTraversed(game, Info.Address) &&
                !GateUnlockRequirements.IsUnlocked(game, Info.Address)) return "?";
            if (!string.IsNullOrWhiteSpace(Info.DestinationRegion)) return Info.DestinationRegion.ToUpperInvariant();
            int underscore = Info.DestinationRoom.IndexOf('_');
            return underscore > 0 ? Info.DestinationRoom.Substring(0, underscore).ToUpperInvariant() : "OUT";
        }
    }
}
