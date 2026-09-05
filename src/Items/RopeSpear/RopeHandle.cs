using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class RopeHandle : PlayerCarryableItem, IDrawable
{
    private const float Radius = 4.2f;
    private const float Mass = 0.055f;

    // A wallbehind tile only tells gameplay that there is a background wall. The
    // rendered room texture still contains the exact 30-step depth at that pixel.
    // Anchored RopeHandles sample that depth per camera and use CustomDepth so the
    // marker is occluded by anything genuinely in front of the wall it is pinned to.
    private const float BackgroundDepthFallback = 10f / 30f;
    private const float AnchorDepthFrontBias = 0.5f / 30f;

    internal RopeHandle(AbstractPhysicalObject abstractPhysicalObject)
        : base(abstractPhysicalObject)
    {
        bodyChunks = new BodyChunk[1];
        bodyChunks[0] = new BodyChunk(this, 0, Vector2.zero, Radius, Mass);
        bodyChunkConnections = new BodyChunkConnection[0];
        airFriction = 0.995f;
        gravity = 0.9f;
        bounce = 0.18f;
        surfaceFriction = 0.7f;
        collisionLayer = 1;
        waterFriction = 0.95f;
        buoyancy = 0.7f;
    }

    private AbstractRopeHandle Data => abstractPhysicalObject as AbstractRopeHandle;

    internal EntityID ParentSpearID => Data?.ParentSpearID ?? default;

    internal bool Anchored => Data?.Anchored ?? false;

    internal Player Holder
    {
        get
        {
            for (int i = 0; i < grabbedBy.Count; i++)
            {
                if (grabbedBy[i]?.grabber is Player player)
                {
                    return player;
                }
            }

            return null;
        }
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);

        Vector2 position = Data != null && Data.Anchored
            ? Data.AnchorPosition
            : placeRoom.MiddleOfTile(abstractPhysicalObject.pos);
        firstChunk.HardSetPosition(position);
        firstChunk.lastPos = position;
        firstChunk.vel = Vector2.zero;
        firstChunk.collideWithTerrain = Data == null || !Data.Anchored;
        GoThroughFloors = false;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        Player holder = Holder;
        if (holder != null)
        {
            if (Data != null && Data.Anchored)
            {
                Data.Anchored = false;
            }

            firstChunk.collideWithTerrain = true;

            // While this endpoint is in the player's hand, Alt reserves directional
            // input for RopeSpear reeling. In particular Alt+Up must remain a
            // shorten-rope command instead of being consumed by VineGrab climbing.
            // Do not alter holder.input here: RopeSpear.HandleReelingInput still
            // needs to read the original Up/Down direction later in the frame.
            bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (altHeld)
            {
                holder.vineGrabDelay = Mathf.Max(holder.vineGrabDelay, 2);

                if (holder.animation == Player.AnimationIndex.VineGrab &&
                    holder.vinePos?.vine is RopeSpear ropeSpear &&
                    ropeSpear.abstractPhysicalObject != null &&
                    ropeSpear.abstractPhysicalObject.ID == ParentSpearID)
                {
                    holder.animation = Player.AnimationIndex.None;
                    holder.vinePos = null;
                    holder.vineGrabDelay = Mathf.Max(holder.vineGrabDelay, 10);
                    holder.noGrabCounter = Mathf.Max(holder.noGrabCounter, 5);
                }
            }

            if (holder.enteringShortCut.HasValue || holder.inShortcut)
            {
                for (int i = grabbedBy.Count - 1; i >= 0; i--)
                {
                    Creature.Grasp grasp = grabbedBy[i];
                    if (grasp?.grabber == holder)
                    {
                        holder.ReleaseGrasp(grasp.graspUsed);
                    }
                }

                firstChunk.vel *= 0.35f;
                GoThroughFloors = false;
            }

            return;
        }

        // PhysicalObject.Grabbed sets GoThroughFloors=true. Generic carryable items
        // do not reset it when a grasp is released, so explicitly restore ordinary
        // floor collision once this handle is loose in the room.
        GoThroughFloors = false;

        if (Data != null && Data.Anchored)
        {
            firstChunk.collideWithTerrain = false;
            firstChunk.HardSetPosition(Data.AnchorPosition);
            firstChunk.lastPos = Data.AnchorPosition;
            firstChunk.vel = Vector2.zero;
        }
        else
        {
            firstChunk.collideWithTerrain = true;
        }
    }

    public override void PickedUp(Creature upPicker)
    {
        base.PickedUp(upPicker);

        if (Data != null)
        {
            Data.Anchored = false;
        }

        firstChunk.collideWithTerrain = true;
    }

    internal bool TryAnchorToNearbyTerrain()
    {
        if (room == null || Data == null)
        {
            return false;
        }

        // Capture this before the throwing hook releases the grasp. The safety
        // runtime uses the actual anchor transition rather than depending on which
        // Player.ThrowObject hook happened to run first.
        Player holderAtAnchor = Holder;
        Vector2 position = firstChunk.pos;

        // RopeHandle anchoring is a background-wall operation, not a foreground
        // collision operation. The endpoint may be completely suspended in open
        // foreground space; it only needs a non-empty room background at the exact
        // point where the player is holding it. Rain World's Tile.wallbehind is the
        // authoritative marker for that background wall.
        if (room.GetTile(position).Solid && lastOutsideTerrainPos.HasValue)
        {
            position = lastOutsideTerrainPos.Value;
        }

        Room.Tile tile = room.GetTile(position);
        if (tile.Solid || !tile.wallbehind)
        {
            return false;
        }

        return CommitAnchor(position, holderAtAnchor);
    }

    private bool CommitAnchor(Vector2 position, Player holderAtAnchor)
    {
        if (room == null || Data == null)
        {
            return false;
        }

        Data.Anchored = true;
        Data.AnchorPosition = position;
        firstChunk.HardSetPosition(position);
        firstChunk.lastPos = position;
        firstChunk.vel = Vector2.zero;
        firstChunk.collideWithTerrain = false;
        GoThroughFloors = false;

        RopeSpearHandleAnchorSafetyRuntime.NotifyHandleAnchored(this, holderAtAnchor);

        room.PlaySound(SoundID.Spear_Stick_In_Wall, firstChunk, loop: false, 0.45f, 1.25f);
        return true;
    }

    public void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        sLeaser.sprites = new FSprite[2];
        sLeaser.sprites[0] = new FSprite("pixel")
        {
            scaleX = 8f,
            scaleY = 3f
        };
        sLeaser.sprites[1] = new FSprite("pixel")
        {
            scaleX = 3f,
            scaleY = 7f
        };
        AddToContainer(sLeaser, rCam, null);
    }

    public void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        Vector2 worldPosition = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker);
        UpdateDepthRendering(sLeaser, rCam, worldPosition);

        Vector2 position = worldPosition - camPos;
        sLeaser.sprites[0].SetPosition(position);
        sLeaser.sprites[1].SetPosition(position);
        sLeaser.sprites[1].rotation = Anchored
            ? 90f
            : Custom.VecToDeg(firstChunk.vel.sqrMagnitude > 0.01f ? firstChunk.vel : Vector2.right);

        if (slatedForDeletetion || room != rCam.room)
        {
            sLeaser.CleanSpritesAndRemove();
        }
    }

    private void UpdateDepthRendering(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        Vector2 worldPosition)
    {
        if (sLeaser?.sprites == null || rCam == null)
        {
            return;
        }

        bool anchored = Anchored;
        FContainer targetContainer = rCam.ReturnFContainer(anchored ? "Foreground" : "Items");
        FShader shader = anchored
            ? rCam.room.game.rainWorld.Shaders["CustomDepth"]
            : FShader.defaultShader;

        float alpha = 1f;
        if (anchored)
        {
            // RoomCamera.DepthAtCoordinate decodes the exact level-texture depth
            // (0..29 / 30), not merely the gameplay wallbehind bit. Pull the marker
            // half a depth step toward the camera so it remains visible on the wall
            // surface while still being hidden by genuinely closer geometry.
            float backgroundDepth = ResolveAnchorDepth(rCam, worldPosition);
            float markerDepth = Mathf.Clamp01(backgroundDepth - AnchorDepthFrontBias);
            alpha = 1f - markerDepth;
        }

        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            FSprite sprite = sLeaser.sprites[i];
            if (sprite == null)
            {
                continue;
            }

            if (sprite.container != targetContainer)
            {
                sprite.RemoveFromContainer();
                targetContainer.AddChild(sprite);
            }

            sprite.shader = shader;
            sprite.alpha = alpha;
        }
    }

    private float ResolveAnchorDepth(RoomCamera rCam, Vector2 worldPosition)
    {
        if (rCam?.room != room)
        {
            return BackgroundDepthFallback;
        }

        // DepthAtCoordinate indexes the current camera's level texture directly, so
        // only sample while this world point is inside that texture. Off-screen
        // leasers use the ordinary wallbehind depth until the point becomes visible.
        if (!rCam.PositionCurrentlyVisible(worldPosition, 0f, widescreen: true))
        {
            return BackgroundDepthFallback;
        }

        return Mathf.Clamp01(rCam.DepthAtCoordinate(worldPosition));
    }

    public void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        Color handle = Color.Lerp(palette.blackColor, new Color(0.55f, 0.42f, 0.24f), 0.62f);
        sLeaser.sprites[0].color = handle;
        sLeaser.sprites[1].color = Color.Lerp(handle, palette.blackColor, 0.35f);
    }

    public void AddToContainer(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        FContainer newContainer)
    {
        FContainer container = newContainer ?? rCam.ReturnFContainer(Anchored ? "Foreground" : "Items");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].RemoveFromContainer();
            container.AddChild(sLeaser.sprites[i]);
        }
    }
}
