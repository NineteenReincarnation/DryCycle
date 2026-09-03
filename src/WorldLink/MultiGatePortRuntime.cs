using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.WorldLink;

internal sealed class MultiGatePortRuntime : UpdatableAndDeletable, IDrawable
{
    internal readonly PlacedObject Placed;
    internal readonly MultiGatePortData Data;

    internal float MechanicalFactor { get; private set; }
    internal float LastMechanicalFactor { get; private set; }
    internal float OpenFactor { get; private set; }
    internal float LastOpenFactor { get; private set; }
    internal bool Denied { get; private set; }

    internal WorldLinkPortAddress Address => Data.Address(room?.abstractRoom?.name ?? string.Empty);
    internal bool ShouldRender => Placed != null && (Placed.active || MechanicalFactor > 0.0001f);
    internal bool ShouldCollide => Placed != null && (Placed.active || MechanicalFactor > 0.0001f);

    internal MultiGatePortRuntime(Room room, PlacedObject placed, MultiGatePortData data)
    {
        this.room = room;
        Placed = placed;
        Data = data;
        ValidateVanillaNode();
        ValidateFrameSupports();
    }

    internal static float DoorOpenFromMechanical(float mechanical)
    {
        // First 18%: locks/actuators release while the physical gate stays sealed.
        // 18-88%: the visible leaf and collider aperture move together.
        // Last 12%: rails/poles finish after passage is already fully open.
        float t = Mathf.InverseLerp(0.18f, 0.88f, Mathf.Clamp01(mechanical));
        return t * t * (3f - 2f * t);
    }

    internal float PreviewOpenFromMechanical(float mechanical) => DoorOpenFromMechanical(mechanical);

    internal void SetMechanicalFactor(float value)
    {
        float oldMechanical = MechanicalFactor;
        float oldOpen = OpenFactor;
        LastMechanicalFactor = oldMechanical;
        LastOpenFactor = oldOpen;
        MechanicalFactor = Mathf.Clamp01(value);
        OpenFactor = DoorOpenFromMechanical(MechanicalFactor);
        WorldLinkGateGraphics.OnMechanicalFactorChanged(this, oldMechanical, MechanicalFactor);
    }

    internal void HoldCurrentPose()
    {
        // A blocked anti-crush frame is a genuinely stationary frame. Reset both
        // interpolation histories so rendering and SurfaceVelocityAt cannot replay the
        // previous movement while the gate is stalled.
        LastMechanicalFactor = MechanicalFactor;
        LastOpenFactor = OpenFactor;
    }

    internal void SetDenied(bool denied) => Denied = denied;

    internal bool IsWithinTransitEnvelope(Vector2 pos, float scale = 1f)
    {
        Vector2 d = pos - Placed.pos;
        return Mathf.Abs(Vector2.Dot(d, Data.Tangent)) <= Data.PassageWidth * 0.5f * scale + 24f &&
               Mathf.Abs(Vector2.Dot(d, Data.Normal)) <= Data.TriggerDepth * scale + 30f;
    }

    internal bool AllProgressPlayersOnSide(bool interior)
    {
        if (room?.game == null) return false;
        List<AbstractCreature> players = room.game.PlayersToProgressOrWin;
        if (players == null || players.Count == 0) return false;
        int seen = 0;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i]?.realizedCreature is not Player p || p.room != room) return false;
            Vector2 d = p.mainBodyChunk.pos - Placed.pos;
            float lateral = Mathf.Abs(Vector2.Dot(d, Data.Tangent));
            float depth = Vector2.Dot(d, Data.Normal);
            bool inZone = lateral <= Data.PassageWidth * 0.47f &&
                          (interior
                              ? depth <= -Data.PanelThickness * 0.25f && depth >= -Data.TriggerDepth
                              : depth >= Data.PanelThickness * 0.25f && depth <= Data.TriggerDepth);
            if (!inZone) return false;
            seen++;
        }
        return seen > 0;
    }

    internal bool AllPresentProgressPlayersOutsideOrGone()
    {
        if (room?.game == null) return false;
        List<AbstractCreature> players = room.game.PlayersToProgressOrWin;
        bool any = false;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i]?.realizedCreature is not Player p || p.room != room) continue;
            any = true;
            Vector2 d = p.mainBodyChunk.pos - Placed.pos;
            if (Vector2.Dot(d, Data.Normal) < Data.PanelThickness * 0.25f) return false;
        }
        return any || players.Count > 0;
    }

    internal bool ProgressPlayersStandingStill()
    {
        if (room?.game == null) return false;
        List<AbstractCreature> players = room.game.PlayersToProgressOrWin;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i]?.realizedCreature is not Player p || p.room != room) return false;
            if (p.touchedNoInputCounter < 20 && p.onBack == null) return false;
        }
        return players.Count > 0;
    }

    internal bool WouldCrushPhysicalObject(float proposedOpenFactor)
    {
        if (room?.physicalObjects == null) return false;
        float half = Data.PassageWidth * 0.5f;
        float aperture = half * Mathf.Clamp01(proposedOpenFactor);

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> list = room.physicalObjects[layer];
            for (int i = 0; i < list.Count; i++)
            {
                PhysicalObject obj = list[i];
                if (obj?.bodyChunks == null) continue;
                for (int c = 0; c < obj.bodyChunks.Length; c++)
                {
                    BodyChunk chunk = obj.bodyChunks[c];
                    Vector2 d = chunk.pos - Placed.pos;
                    float u = Mathf.Abs(Vector2.Dot(d, Data.Tangent));
                    float v = Mathf.Abs(Vector2.Dot(d, Data.Normal));
                    if (u > aperture - chunk.rad && u < half + chunk.rad &&
                        v < Data.PanelThickness * 0.5f + chunk.rad)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    internal bool TryGetLeaf(int side, bool previous, out GateLeaf leaf)
    {
        float open = previous ? LastOpenFactor : OpenFactor;
        float half = Data.PassageWidth * 0.5f;
        float inner = half * open;
        float length = Mathf.Max(0f, half - inner);
        if (length < 0.1f)
        {
            leaf = default;
            return false;
        }

        float sign = side < 0 ? -1f : 1f;
        float centerU = sign * (inner + length * 0.5f);
        leaf = new GateLeaf(
            Placed.pos + Data.Tangent * centerU,
            Data.Tangent,
            Data.Normal,
            length * 0.5f,
            Data.PanelThickness * 0.5f,
            side,
            half,
            open);
        return true;
    }

    internal Vector2 SurfaceVelocityAt(GateLeaf leaf, Vector2 point)
    {
        float delta = OpenFactor - LastOpenFactor;
        if (Mathf.Abs(delta) < 0.000001f) return Vector2.zero;

        float sign = leaf.Side < 0 ? -1f : 1f;
        float outer = sign * leaf.OuterHalfWidth;
        float inner = sign * leaf.OuterHalfWidth * OpenFactor;
        float u = Vector2.Dot(point - Placed.pos, Data.Tangent);
        float denominator = inner - outer;
        float s = Mathf.Abs(denominator) < 0.0001f ? 0f : Mathf.Clamp01((u - outer) / denominator);
        return Data.Tangent * (sign * leaf.OuterHalfWidth * delta * s);
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        if (!WorldLinkRoomRegistry.Enabled || Placed == null || room?.roomSettings?.placedObjects == null)
        {
            Destroy();
            return;
        }

        if (!room.roomSettings.placedObjects.Contains(Placed))
        {
            // The owning controller is allowed to finish a safe close after mapper
            // deletion. Once fully mechanically closed, the drawable can retire.
            if (MechanicalFactor <= 0.0001f) Destroy();
            return;
        }

        // Data.Enabled is route availability only. It must never instantly mutate the
        // physical aperture. The controller observes the flag and safely closes an
        // in-flight transaction using WouldCrushPhysicalObject.
        if (!Data.Enabled) SetDenied(false);
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam) =>
        WorldLinkGateGraphics.InitiateSprites(this, sLeaser, rCam);

    public void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos) =>
        WorldLinkGateGraphics.DrawSprites(this, sLeaser, rCam, timeStacker, camPos);

    public void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette) =>
        WorldLinkGateGraphics.ApplyPalette(this, sLeaser, rCam, palette);

    public void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer) =>
        WorldLinkGateGraphics.AddToContainer(this, sLeaser, rCam, newContainer);

    private void ValidateVanillaNode()
    {
        if (Data.TransitMode != WorldLinkTransitMode.VanillaNode || room?.abstractRoom == null) return;
        if (Data.VanillaNodeIndex < 0 || room.abstractRoom.connections == null || Data.VanillaNodeIndex >= room.abstractRoom.connections.Length)
        {
            Plugin.Logger?.LogWarning($"WorldLink: {Address} has invalid VanillaNode index {Data.VanillaNodeIndex}.");
            return;
        }

        int targetIndex = room.abstractRoom.connections[Data.VanillaNodeIndex];
        if (targetIndex < 0)
        {
            Plugin.Logger?.LogWarning($"WorldLink: {Address} VanillaNode {Data.VanillaNodeIndex} is not connected.");
            return;
        }

        string actual = room.world?.GetAbstractRoom(targetIndex)?.name;
        if (!string.IsNullOrWhiteSpace(Data.DestinationRoom) && !string.IsNullOrWhiteSpace(actual) &&
            !string.Equals(Data.DestinationRoom, actual, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Logger?.LogWarning($"WorldLink: {Address} says destination '{Data.DestinationRoom}', but node {Data.VanillaNodeIndex} connects to '{actual}'. Runtime traversal still follows the vanilla node.");
        }
    }

    private void ValidateFrameSupports()
    {
        if (room == null || Placed == null) return;
        Vector2 tangent = Data.Tangent;
        Vector2 normal = Data.Normal;
        float half = Data.PassageWidth * 0.5f;
        float embed = Mathf.Max(6f, Data.PanelThickness * 0.65f);

        for (int side = -1; side <= 1; side += 2)
        {
            Vector2 basePoint = Placed.pos + tangent * (side * (half + embed));
            bool supported = false;
            for (int sample = -1; sample <= 1 && !supported; sample++)
            {
                Vector2 point = basePoint + normal * (sample * Mathf.Max(4f, Data.PanelThickness * 0.45f));
                supported = room.GetTile(point).Solid;
            }
            if (!supported)
            {
                string which = side < 0 ? "A" : "B";
                Plugin.Logger?.LogWarning($"WorldLink: {Address} frame jamb {which} is not embedded in solid terrain. Move the port or extend surrounding Tile solids.");
            }
        }
    }
}
