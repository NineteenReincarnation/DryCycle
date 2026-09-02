using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal sealed class MultiGatePortRuntime : UpdatableAndDeletable, IDrawable
{
    internal readonly PlacedObject Placed;
    internal readonly MultiGatePortData Data;
    internal float OpenFactor { get; private set; }
    internal float LastOpenFactor { get; private set; }
    internal bool Denied { get; private set; }
    internal WorldLinkPortAddress Address => Data.Address(room?.abstractRoom?.name ?? string.Empty);

    internal MultiGatePortRuntime(Room room, PlacedObject placed, MultiGatePortData data)
    {
        this.room = room;
        Placed = placed;
        Data = data;
        ValidateVanillaNode();
    }

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

    internal void SetOpenFactor(float value)
    {
        LastOpenFactor = OpenFactor;
        OpenFactor = Mathf.Clamp01(value);
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
                          (interior ? depth <= -Data.PanelThickness * 0.25f && depth >= -Data.TriggerDepth
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
                    if (u > aperture - chunk.rad && u < half + chunk.rad && v < Data.PanelThickness * 0.5f + chunk.rad)
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
        leaf = new GateLeaf(Placed.pos + Data.Tangent * centerU, Data.Tangent, Data.Normal, length * 0.5f, Data.PanelThickness * 0.5f, side, half, open);
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
        if (!WorldLinkRoomRegistry.Enabled || Placed == null || room?.roomSettings?.placedObjects == null || !room.roomSettings.placedObjects.Contains(Placed))
        {
            SlateForDeletion();
            return;
        }
        // Enabled is mapper-authored live state. Keep the runtime object alive when it
        // is toggled off so switching it back on in DevUI does not require reloading the room.
        if (!Placed.active || !Data.Enabled)
        {
            SetDenied(false);
            SetOpenFactor(0f);
            return;
        }
        // If no controller owns this port, it deliberately remains fully closed.
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        // 0/1 are the exact visible surfaces backed by OrientedGateCollision. 2/3 are
        // short jambs placed beyond the traversable width and intended to overlap the
        // mapper's solid Tile frame. No non-colliding decoration crosses the aperture.
        sLeaser.sprites = new FSprite[5];
        for (int i = 0; i < 4; i++)
        {
            sLeaser.sprites[i] = new FSprite("pixel") { anchorX = 0.5f, anchorY = 0.5f };
        }
        sLeaser.sprites[4] = CreateGlyphSprite();
        AddToContainer(sLeaser, rCam, null);
    }

    public void DrawSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
    {
        bool visible = Placed?.active == true && Data.Enabled;
        for (int i = 0; i < sLeaser.sprites.Length; i++) sLeaser.sprites[i].isVisible = visible;
        if (!visible)
        {
            if (slatedForDeletetion || room != rCam.room) sLeaser.CleanSpritesAndRemove();
            return;
        }

        float open = Mathf.Lerp(LastOpenFactor, OpenFactor, timeStacker);
        DrawLeaf(sLeaser.sprites[0], -1, open, camPos);
        DrawLeaf(sLeaser.sprites[1], 1, open, camPos);

        DrawFrameJamb(sLeaser.sprites[2], -1, camPos);
        DrawFrameJamb(sLeaser.sprites[3], 1, camPos);

        FSprite glyph = sLeaser.sprites[4];
        WorldLinkGlyphs.Refresh(glyph, Address);
        Vector2 gp = Placed.pos + Data.GlyphOffset - camPos;
        glyph.x = gp.x; glyph.y = gp.y;
        glyph.color = Denied
            ? Color.Lerp(Color.red, Color.white, 0.35f + 0.35f * Mathf.Sin((room.game.clock + timeStacker) / 7f))
            : Color.Lerp(new Color(0.65f, 0.65f, 0.7f), Color.white, 0.35f + 0.25f * Mathf.Sin((room.game.clock + timeStacker) / 14f));

        if (slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    public void ApplyPalette(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
    {
        sLeaser.sprites[0].color = palette.blackColor;
        sLeaser.sprites[1].color = palette.blackColor;
        sLeaser.sprites[2].color = Color.Lerp(palette.blackColor, Color.white, 0.08f);
        sLeaser.sprites[3].color = Color.Lerp(palette.blackColor, Color.white, 0.08f);
    }

    public void AddToContainer(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, FContainer newContainer)
    {
        newContainer ??= rCam.ReturnFContainer("Items");
        for (int i = 0; i < sLeaser.sprites.Length; i++) newContainer.AddChild(sLeaser.sprites[i]);
    }

    private FSprite CreateGlyphSprite() => WorldLinkGlyphs.Create(Address);

    private void DrawFrameJamb(FSprite sprite, int side, Vector2 camPos)
    {
        float sign = side < 0 ? -1f : 1f;
        Vector2 center = Placed.pos + Data.Tangent * (sign * (Data.PassageWidth * 0.5f + 4f));
        sprite.x = center.x - camPos.x;
        sprite.y = center.y - camPos.y;
        sprite.rotation = Custom.VecToDeg(Data.Normal);
        sprite.scaleX = Mathf.Max(18f, Data.PanelThickness * 2.4f);
        sprite.scaleY = 8f;
    }

    private void DrawLeaf(FSprite sprite, int side, float open, Vector2 camPos)
    {
        float half = Data.PassageWidth * 0.5f;
        float inner = half * open;
        float length = Mathf.Max(0f, half - inner);
        float sign = side < 0 ? -1f : 1f;
        Vector2 center = Placed.pos + Data.Tangent * (sign * (inner + length * 0.5f));
        sprite.x = center.x - camPos.x;
        sprite.y = center.y - camPos.y;
        sprite.rotation = Custom.VecToDeg(Data.Tangent);
        sprite.scaleX = length;
        sprite.scaleY = Data.PanelThickness;
        sprite.isVisible = length > 0.1f;
    }
}
