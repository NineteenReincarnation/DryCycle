using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class RopeHandle : PlayerCarryableItem, IDrawable
{
    private const float Radius = 4.2f;
    private const float Mass = 0.055f;

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
        Vector2 position = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker) - camPos;
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
        FContainer container = newContainer ?? rCam.ReturnFContainer("Items");
        for (int i = 0; i < sLeaser.sprites.Length; i++)
        {
            sLeaser.sprites[i].RemoveFromContainer();
            container.AddChild(sLeaser.sprites[i]);
        }
    }
}
