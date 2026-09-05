using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class RopeSpear : Spear, IClimbableVine
{
    private const int RopeMeshSegments = RopeSpearRopeSystem.NodeCount - 1;
    private const float RopeThickness = 1.15f;
    private const float ReelSpeed = 2.15f;
    private const float ClimbSpeed = 2.65f;
    private const float TensionSpring = 0.032f;
    private const float TensionDamping = 0.06f;
    private const float MaxPlayerPullImpulse = 2.4f;
    private const float MaxAnchorPullImpulse = 1.8f;
    private const float FlightSimulationSlack = 420f;
    private const float SettledPayoutSlack = 26f;
    private const float SpearMountRange = 44f;

    private readonly RopeSpearRopeSystem _ropeSystem = new();
    private readonly Vector2[] _drawNodes = new Vector2[RopeSpearRopeSystem.NodeCount];

    private RopeHandle _handle;
    private Player _pendingHandleOwner;
    private Room _vineRoom;
    private int _ropeSpriteIndex = -1;
    private float _lastRouteLength;
    private float _preThrowRopeLength;
    private bool _ropeDeployed;
    private bool _flightPayout;

    public RopeSpear(AbstractPhysicalObject abstractPhysicalObject, World world)
        : base(abstractPhysicalObject, world)
    {
    }

    private AbstractRopeSpear Data => abstractPhysicalObject as AbstractRopeSpear;

    internal bool RopeActive =>
        _ropeDeployed &&
        !IsBroken &&
        _handle != null &&
        !_handle.slatedForDeletetion &&
        room != null &&
        _handle.room == room &&
        _ropeSystem.Ready;

    private bool IsBroken => Data?.RopeBroken ?? true;

    private float RopeLength
    {
        get => Data?.RopeLength ?? AbstractRopeSpear.DefaultRopeLength;
        set
        {
            if (Data != null)
            {
                Data.RopeLength = Mathf.Clamp(
                    value,
                    AbstractRopeSpear.MinRopeLength,
                    AbstractRopeSpear.MaxRopeLength);
            }
        }
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);
        EnsureVineRegistration(placeRoom);
    }

    public override void NewRoom(Room newRoom)
    {
        RemoveVineRegistration();
        base.NewRoom(newRoom);
        EnsureVineRegistration(newRoom);
    }

    public override void Destroy()
    {
        RemoveVineRegistration();
        _ropeSystem.Reset();
        base.Destroy();
    }

    public override void Thrown(
        Creature thrownBy,
        Vector2 thrownPos,
        Vector2? firstFrameTraceFromPos,
        IntVector2 throwDir,
        float frc,
        bool eu)
    {
        base.Thrown(thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, frc, eu);

        if (thrownBy is not Player player || IsBroken)
        {
            return;
        }

        if (Data != null)
        {
            Data.HasPersistentHandleAnchor = false;
            Data.PersistentHandleAnchor = Vector2.zero;
        }

        _ropeDeployed = true;
        _pendingHandleOwner = player;
        _preThrowRopeLength = RopeLength;
        _flightPayout = true;
        EnsureHandle(player);
        _ropeSystem.Reset();
    }

    public override void PickedUp(Creature upPicker)
    {
        base.PickedUp(upPicker);

        if (Data != null)
        {
            Data.RopeBroken = false;
            Data.RopeLength = AbstractRopeSpear.DefaultRopeLength;
            Data.HasPersistentHandleAnchor = false;
            Data.PersistentHandleAnchor = Vector2.zero;
        }

        _ropeDeployed = false;
        _pendingHandleOwner = null;
        _flightPayout = false;
        _preThrowRopeLength = AbstractRopeSpear.DefaultRopeLength;
        _ropeSystem.Reset();
        RemoveAssociatedHandles();
        _handle = null;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        EnsureVineRegistration(room);

        if (!_ropeDeployed && !IsBroken)
        {
            if (!TryResolveExistingHandle() &&
                Data?.HasPersistentHandleAnchor == true &&
                mode == Mode.StuckInWall)
            {
                CreateHandle(Data.PersistentHandleAnchor, anchored: true);
            }

            if (_handle != null && !_handle.slatedForDeletetion)
            {
                _ropeDeployed = true;
            }
        }

        if (!_ropeDeployed || IsBroken || room == null)
        {
            return;
        }

        if (_handle == null || _handle.slatedForDeletetion)
        {
            if (!TryResolveExistingHandle())
            {
                if (Data?.HasPersistentHandleAnchor == true && mode == Mode.StuckInWall)
                {
                    CreateHandle(Data.PersistentHandleAnchor, anchored: true);
                }

                if (_handle == null)
                {
                    return;
                }
            }
        }

        if (_handle.room != room)
        {
            BreakRope();
            return;
        }

        if (AttachedCreatureEnteredShortcut())
        {
            BreakRope();
            return;
        }

        TryGiveHandleToPendingOwner();
        if (mode != Mode.Thrown)
        {
            HandleReelingInput();
        }
        SynchronizePersistentAnchorState();

        Vector2 handlePoint = GetHandleRopePoint(1f);
        Vector2 spearPoint = GetSpearRopePoint(1f);
        bool spearFlying = mode == Mode.Thrown;

        // The projectile never feels a rope-length cap while it is in Thrown mode.
        // Give the simulated chain generous temporary slack, then record only the
        // distance actually paid out. This prevents the first taut frame from
        // pulling a left/right throw back into the player or a nearby corner.
        float simulationLength = RopeLength;
        if (spearFlying)
        {
            simulationLength = Mathf.Max(
                simulationLength,
                Vector2.Distance(handlePoint, spearPoint) + FlightSimulationSlack);
        }

        _ropeSystem.Update(room, handlePoint, spearPoint, simulationLength, RopeThickness);

        float routeLength = _ropeSystem.RouteLength;
        if (spearFlying)
        {
            RopeLength = Mathf.Max(RopeLength, routeLength + SettledPayoutSlack);
            _lastRouteLength = routeLength;
            return;
        }

        if (_flightPayout)
        {
            RopeLength = Mathf.Max(
                Mathf.Max(_preThrowRopeLength, RopeLength),
                routeLength + SettledPayoutSlack);
            _flightPayout = false;
        }

        float stretch = routeLength - RopeLength;
        if (stretch > 0.75f)
        {
            ApplyRopeConstraint(stretch, routeLength);
        }

        _lastRouteLength = routeLength;
    }

    internal bool TryFindNearestRopePoint(
        Vector2 worldPosition,
        float maxDistance,
        out float normalizedPosition,
        out float distance)
    {
        normalizedPosition = 0f;
        distance = float.MaxValue;
        return RopeActive &&
               mode != Mode.Thrown &&
               _ropeSystem.TryFindNearestPoint(
                   worldPosition,
                   maxDistance,
                   out normalizedPosition,
                   out distance);
    }

    internal bool UpdatePlayerRopeGrab(Player player, ref float normalizedPosition)
    {
        if (!RopeActive ||
            player == null ||
            player.room != room ||
            player.dead ||
            player.inShortcut ||
            player.enteringShortCut.HasValue ||
            mode == Mode.Thrown ||
            _handle?.Holder == player)
        {
            return false;
        }

        if (player.input == null || player.input.Length == 0)
        {
            return false;
        }

        Player.InputPackage input = player.input[0];
        Player.InputPackage previousInput = player.input.Length > 1
            ? player.input[1]
            : default;

        if (input.jmp && !previousInput.jmp)
        {
            // Jump is an intentional rope release. Preserve the player's existing
            // swing velocity and add only a small upward kick.
            player.mainBodyChunk.vel.y = Mathf.Max(player.mainBodyChunk.vel.y, 3.2f);
            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                player.bodyChunks[1].vel.y = Mathf.Max(player.bodyChunks[1].vel.y, 2.5f);
            }
            return false;
        }

        if (input.y != 0)
        {
            normalizedPosition = AdvanceGrabPosition(normalizedPosition, input.y);
        }

        if (input.y > 0 && TryMountSpearFromRope(player, normalizedPosition))
        {
            return false;
        }

        Vector2 ropePoint = _ropeSystem.GetPoint(normalizedPosition);
        Vector2 targetBodyPosition = ropePoint + new Vector2(0f, -11f);
        Vector2 delta = targetBodyPosition - player.mainBodyChunk.pos;
        if (delta.magnitude > 105f)
        {
            return false;
        }

        // Soft attachment: strong enough to keep the hand/body on the rope, but it
        // never HardSetPositions the slugcat. Momentum and gravity remain available
        // for swinging, and the rope receives the opposite local deformation.
        Vector2 pull = Vector2.ClampMagnitude(delta * 0.145f, 3.5f);
        player.mainBodyChunk.vel += pull;
        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            player.bodyChunks[1].vel += pull * 0.76f;
        }

        if (input.x != 0)
        {
            float swing = input.x * 0.115f;
            player.mainBodyChunk.vel.x += swing;
            if (player.bodyChunks != null && player.bodyChunks.Length > 1)
            {
                player.bodyChunks[1].vel.x += swing * 0.72f;
            }
        }

        player.standing = false;
        _ropeSystem.ApplyExternalPull(
            normalizedPosition,
            player.mainBodyChunk.pos + new Vector2(0f, 7f),
            0.095f);
        return true;
    }

    private float AdvanceGrabPosition(float current, int verticalInput)
    {
        float referenceLength = Mathf.Max(80f, _ropeSystem.RouteLength);
        float step = Mathf.Clamp(ClimbSpeed / referenceLength, 0.004f, 0.035f);
        float lowerT = Mathf.Clamp01(current - step);
        float upperT = Mathf.Clamp01(current + step);
        Vector2 lower = _ropeSystem.GetPoint(lowerT);
        Vector2 upper = _ropeSystem.GetPoint(upperT);

        float direction;
        if (Mathf.Abs(upper.y - lower.y) > 0.45f)
        {
            direction = upper.y > lower.y ? 1f : -1f;
        }
        else
        {
            // Near-horizontal local sections have no meaningful world-up tangent.
            // Keep the previous endpoint convention so holding Up does not jitter
            // back and forth from frame to frame.
            direction = GetSpearRopePoint(1f).y >= GetHandleRopePoint(1f).y ? 1f : -1f;
        }

        if (verticalInput < 0)
        {
            direction = -direction;
        }

        return Mathf.Clamp01(current + step * direction);
    }

    private bool TryMountSpearFromRope(Player player, float normalizedPosition)
    {
        if (mode != Mode.StuckInWall ||
            Data == null ||
            Data.stuckInWallCycles < 0 ||
            normalizedPosition < 0.88f)
        {
            return false;
        }

        Vector2 ropePoint = _ropeSystem.GetPoint(normalizedPosition);
        Vector2 spearPoint = GetSpearRopePoint(1f);
        if (!Custom.DistLess(ropePoint, spearPoint, SpearMountRange) ||
            spearPoint.y < player.mainBodyChunk.pos.y - 14f)
        {
            return false;
        }

        if (!TryFindHorizontalSpearBeam(player.room, firstChunk.pos, player.mainBodyChunk.pos, out Vector2 beamCenter))
        {
            return false;
        }

        player.noGrabCounter = Mathf.Max(player.noGrabCounter, 15);
        player.forceFeetToHorizontalBeamTile = 20;
        player.pullupSoftlockSafety = 0;
        player.straightUpOnHorizontalBeam = true;
        player.upOnHorizontalBeamPos = new Vector2(
            beamCenter.x,
            player.room.MiddleOfTile(beamCenter).y + 20f);
        player.animation = Player.AnimationIndex.GetUpOnBeam;
        player.bodyMode = Player.BodyModeIndex.ClimbingOnBeam;
        player.standing = false;

        player.mainBodyChunk.pos = beamCenter;
        player.mainBodyChunk.lastPos = beamCenter;
        player.mainBodyChunk.vel = Vector2.zero;

        if (player.bodyChunks != null && player.bodyChunks.Length > 1)
        {
            Vector2 lower = beamCenter + new Vector2(0f, -17f);
            player.bodyChunks[1].pos = lower;
            player.bodyChunks[1].lastPos = lower;
            player.bodyChunks[1].vel = Vector2.zero;
        }

        player.room.PlaySound(
            SoundID.Slugcat_Get_Up_On_Horizontal_Beam,
            player.mainBodyChunk,
            loop: false,
            0.75f,
            1f);
        return true;
    }

    private static bool TryFindHorizontalSpearBeam(
        Room targetRoom,
        Vector2 spearPosition,
        Vector2 playerPosition,
        out Vector2 beamCenter)
    {
        beamCenter = Vector2.zero;
        if (targetRoom == null)
        {
            return false;
        }

        IntVector2 origin = targetRoom.GetTilePosition(spearPosition);
        float bestDistance = float.MaxValue;
        bool found = false;

        for (int x = -2; x <= 2; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                IntVector2 tilePos = origin + new IntVector2(x, y);
                Room.Tile tile = targetRoom.GetTile(tilePos);
                if (!tile.horizontalBeam || tile.Solid)
                {
                    continue;
                }

                Vector2 center = targetRoom.MiddleOfTile(tilePos);
                float distance = Vector2.Distance(playerPosition, center);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                beamCenter = center;
                found = true;
            }
        }

        return found;
    }

    private bool AttachedCreatureEnteredShortcut()
    {
        return mode == Mode.StuckInCreature &&
               stuckInObject is Creature creature &&
               (creature.enteringShortCut.HasValue || creature.inShortcut);
    }

    private void HandleReelingInput()
    {
        Player holder = _handle?.Holder;
        if (holder?.input == null || holder.input.Length == 0)
        {
            return;
        }

        bool altHeld = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (!altHeld)
        {
            return;
        }

        if (holder.input[0].y > 0)
        {
            RopeLength -= ReelSpeed;
        }
        else if (holder.input[0].y < 0)
        {
            RopeLength += ReelSpeed;
        }
    }

    private void SynchronizePersistentAnchorState()
    {
        if (Data == null || _handle == null)
        {
            return;
        }

        if (mode == Mode.StuckInWall && _handle.Anchored)
        {
            Data.HasPersistentHandleAnchor = true;
            Data.PersistentHandleAnchor = _handle.firstChunk.pos;
        }
        else if (Data.HasPersistentHandleAnchor)
        {
            Data.HasPersistentHandleAnchor = false;
            Data.PersistentHandleAnchor = Vector2.zero;
        }
    }

    private void ApplyRopeConstraint(float stretch, float routeLength)
    {
        if (!_ropeSystem.Ready || routeLength <= 0.001f)
        {
            return;
        }

        BodyChunk handleChunk = GetMovableHandleChunk(out Player handleHolder);
        BodyChunk spearChunk = GetMovableSpearChunk();

        Vector2 handlePoint = GetHandleRopePoint(1f);
        Vector2 spearPoint = GetSpearRopePoint(1f);
        float segmentFraction = 1f / (RopeSpearRopeSystem.NodeCount - 1f);
        Vector2 nextFromHandle = _ropeSystem.GetPoint(segmentFraction);
        Vector2 nextFromSpear = _ropeSystem.GetPoint(1f - segmentFraction);

        Vector2 handleDirection = Custom.DirVec(handlePoint, nextFromHandle);
        Vector2 spearDirection = Custom.DirVec(spearPoint, nextFromSpear);
        float routeSpeed = routeLength - _lastRouteLength;
        float springImpulse = stretch * TensionSpring + Mathf.Max(0f, routeSpeed) * TensionDamping;

        if (handleChunk != null)
        {
            float impulse = Mathf.Min(MaxPlayerPullImpulse, springImpulse);
            handleChunk.vel += handleDirection * impulse;

            if (handleHolder?.bodyChunks != null && handleHolder.bodyChunks.Length > 1)
            {
                handleHolder.bodyChunks[1].vel += handleDirection * impulse * 0.72f;
            }
        }

        if (spearChunk != null)
        {
            float massScale = Mathf.Clamp01(0.12f / Mathf.Max(0.05f, spearChunk.mass));
            float impulse = Mathf.Min(MaxAnchorPullImpulse, springImpulse * massScale);
            spearChunk.vel += spearDirection * impulse;
        }
    }

    private BodyChunk GetMovableHandleChunk(out Player holder)
    {
        holder = _handle?.Holder;
        if (_handle == null || _handle.Anchored)
        {
            return null;
        }

        return holder?.mainBodyChunk ?? _handle.firstChunk;
    }

    private BodyChunk GetMovableSpearChunk()
    {
        if (mode == Mode.StuckInWall)
        {
            return null;
        }

        if (mode == Mode.StuckInCreature && stuckInObject != null)
        {
            return stuckInChunk;
        }

        return firstChunk;
    }

    private void EnsureHandle(Player owner)
    {
        if (_handle != null && !_handle.slatedForDeletetion)
        {
            return;
        }

        if (TryResolveExistingHandle())
        {
            return;
        }

        Vector2 position = owner?.bodyChunks != null && owner.bodyChunks.Length > 1
            ? owner.bodyChunks[1].pos
            : firstChunk.pos;
        CreateHandle(position, anchored: false);

        if (_handle != null)
        {
            _handle.firstChunk.vel = owner?.mainBodyChunk?.vel ?? Vector2.zero;
        }
    }

    private void CreateHandle(Vector2 position, bool anchored)
    {
        if (room?.abstractRoom == null || room.game == null)
        {
            return;
        }

        AbstractRopeHandle abstractHandle = new(
            room.world,
            room.GetWorldCoordinate(position),
            room.game.GetNewID(),
            abstractPhysicalObject.ID,
            anchored,
            position);

        room.abstractRoom.AddEntity(abstractHandle);
        abstractHandle.RealizeInRoom();
        _handle = abstractHandle.realizedObject as RopeHandle;

        if (_handle != null)
        {
            _handle.firstChunk.HardSetPosition(position);
            _handle.firstChunk.lastPos = position;
            _handle.firstChunk.vel = Vector2.zero;
        }
    }

    private bool TryResolveExistingHandle()
    {
        if (room == null)
        {
            return false;
        }

        if (_handle != null &&
            !_handle.slatedForDeletetion &&
            _handle.ParentSpearID == abstractPhysicalObject.ID)
        {
            return true;
        }

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = room.physicalObjects[layer];
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is RopeHandle handle &&
                    handle.ParentSpearID == abstractPhysicalObject.ID &&
                    !handle.slatedForDeletetion)
                {
                    _handle = handle;
                    return true;
                }
            }
        }

        if (room.abstractRoom?.entities == null)
        {
            return false;
        }

        for (int i = 0; i < room.abstractRoom.entities.Count; i++)
        {
            if (room.abstractRoom.entities[i] is not AbstractRopeHandle abstractHandle ||
                abstractHandle.ParentSpearID != abstractPhysicalObject.ID ||
                abstractHandle.slatedForDeletion)
            {
                continue;
            }

            if (abstractHandle.realizedObject == null)
            {
                abstractHandle.RealizeInRoom();
            }

            _handle = abstractHandle.realizedObject as RopeHandle;
            return _handle != null;
        }

        return false;
    }

    private void TryGiveHandleToPendingOwner()
    {
        if (_pendingHandleOwner == null ||
            _handle == null ||
            _pendingHandleOwner.room != room ||
            _handle.grabbedBy.Count > 0)
        {
            return;
        }

        int freeHand = _pendingHandleOwner.FreeHand();
        if (freeHand < 0)
        {
            return;
        }

        _pendingHandleOwner.SlugcatGrab(_handle, freeHand);
        _pendingHandleOwner = null;
    }

    private void BreakRope()
    {
        if (Data != null)
        {
            Data.RopeBroken = true;
            Data.HasPersistentHandleAnchor = false;
            Data.PersistentHandleAnchor = Vector2.zero;
        }

        _ropeDeployed = false;
        _pendingHandleOwner = null;
        _flightPayout = false;
        _ropeSystem.Reset();
    }

    private void RemoveAssociatedHandles()
    {
        World world = abstractPhysicalObject?.world;
        if (world?.abstractRooms == null)
        {
            return;
        }

        for (int roomIndex = 0; roomIndex < world.abstractRooms.Length; roomIndex++)
        {
            AbstractRoom abstractRoom = world.abstractRooms[roomIndex];
            if (abstractRoom?.entities == null)
            {
                continue;
            }

            for (int i = abstractRoom.entities.Count - 1; i >= 0; i--)
            {
                if (abstractRoom.entities[i] is not AbstractRopeHandle handle ||
                    handle.ParentSpearID != abstractPhysicalObject.ID)
                {
                    continue;
                }

                if (handle.realizedObject != null)
                {
                    handle.realizedObject.AllGraspsLetGoOfThisObject(evenNonExlusive: true);
                    handle.realizedObject.Destroy();
                }

                handle.Destroy();
                abstractRoom.RemoveEntity(handle);
            }
        }
    }

    private Vector2 GetHandleRopePoint(float timeStacker)
    {
        if (_handle == null)
        {
            return firstChunk.pos;
        }

        return Vector2.Lerp(_handle.firstChunk.lastPos, _handle.firstChunk.pos, timeStacker);
    }

    private Vector2 GetSpearRopePoint(float timeStacker)
    {
        Vector2 center = Vector2.Lerp(firstChunk.lastPos, firstChunk.pos, timeStacker);
        Vector2 direction = Vector2.Lerp(lastRotation, rotation, timeStacker);
        if (direction.sqrMagnitude < 0.0001f)
        {
            direction = Vector2.right;
        }
        else
        {
            direction.Normalize();
        }

        return center - direction * 17f;
    }

    private void EnsureVineRegistration(Room targetRoom)
    {
        if (targetRoom == null)
        {
            return;
        }

        if (_vineRoom == targetRoom &&
            targetRoom.climbableVines != null &&
            targetRoom.climbableVines.vines.Contains(this))
        {
            return;
        }

        RemoveVineRegistration();

        if (targetRoom.climbableVines == null)
        {
            targetRoom.climbableVines = new ClimbableVinesSystem();
            targetRoom.AddObject(targetRoom.climbableVines);
        }

        if (!targetRoom.climbableVines.vines.Contains(this))
        {
            targetRoom.climbableVines.vines.Add(this);
        }

        _vineRoom = targetRoom;
    }

    private void RemoveVineRegistration()
    {
        if (_vineRoom?.climbableVines != null)
        {
            _vineRoom.climbableVines.vines.Remove(this);
        }

        _vineRoom = null;
    }

    public int TotalPositions()
    {
        return RopeSpearRopeSystem.NodeCount;
    }

    public Vector2 Pos(int index)
    {
        return _ropeSystem.GetNode(index);
    }

    public float Rad(int index)
    {
        return 2.1f;
    }

    public float Mass(int index)
    {
        return 0.18f;
    }

    public void Push(int index, Vector2 movement)
    {
        _ropeSystem.PushNode(index, movement);
    }

    public void BeingClimbedOn(Creature crit)
    {
    }

    public bool CurrentlyClimbable()
    {
        return RopeActive && mode != Mode.Thrown && !slatedForDeletetion;
    }

    public override void InitiateSprites(RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
    {
        base.InitiateSprites(sLeaser, rCam);

        FSprite[] oldSprites = sLeaser.sprites;
        _ropeSpriteIndex = oldSprites.Length;
        FSprite[] expanded = new FSprite[oldSprites.Length + 1];
        for (int i = 0; i < oldSprites.Length; i++)
        {
            expanded[i] = oldSprites[i];
        }

        expanded[_ropeSpriteIndex] = TriangleMesh.MakeLongMesh(
            RopeMeshSegments,
            pointyTip: false,
            customColor: true);
        sLeaser.sprites = expanded;

        AddToContainer(sLeaser, rCam, null);
        ApplyPalette(sLeaser, rCam, rCam.currentPalette);
    }

    public override void DrawSprites(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        float timeStacker,
        Vector2 camPos)
    {
        base.DrawSprites(sLeaser, rCam, timeStacker, camPos);

        if (_ropeSpriteIndex < 0 ||
            _ropeSpriteIndex >= sLeaser.sprites.Length ||
            sLeaser.sprites[_ropeSpriteIndex] is not TriangleMesh mesh)
        {
            return;
        }

        mesh.isVisible = RopeActive;
        if (!mesh.isVisible)
        {
            return;
        }

        _ropeSystem.CopyPositions(_drawNodes);
        DrawRopeMesh(mesh, _drawNodes, camPos);
    }

    public override void ApplyPalette(
        RoomCamera.SpriteLeaser sLeaser,
        RoomCamera rCam,
        RoomPalette palette)
    {
        base.ApplyPalette(sLeaser, rCam, palette);

        if (_ropeSpriteIndex < 0 ||
            _ropeSpriteIndex >= sLeaser.sprites.Length ||
            sLeaser.sprites[_ropeSpriteIndex] is not TriangleMesh mesh)
        {
            return;
        }

        Color fiber = Color.Lerp(
            palette.blackColor,
            new Color(0.55f, 0.42f, 0.24f),
            0.72f);
        mesh.color = fiber;
        if (mesh.verticeColors != null)
        {
            for (int i = 0; i < mesh.verticeColors.Length; i++)
            {
                mesh.verticeColors[i] = fiber;
            }
        }
    }

    private static void DrawRopeMesh(
        TriangleMesh mesh,
        Vector2[] points,
        Vector2 camPos)
    {
        Vector2 previous = points[0];
        float previousRadius = 0.72f;

        for (int i = 0; i < RopeMeshSegments; i++)
        {
            Vector2 current = points[i + 1];
            Vector2 direction = current - previous;
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = Vector2.right;
            }
            else
            {
                direction.Normalize();
            }

            Vector2 perpendicular = Custom.PerpendicularVector(direction);
            float radius = 0.72f;
            Vector2 middle = (previous + current) * 0.5f;
            Vector2 middlePerp = perpendicular * ((previousRadius + radius) * 0.5f);

            mesh.MoveVertice(i * 4, middle - middlePerp - camPos);
            mesh.MoveVertice(i * 4 + 1, middle + middlePerp - camPos);
            mesh.MoveVertice(i * 4 + 2, current - perpendicular * radius - camPos);
            mesh.MoveVertice(i * 4 + 3, current + perpendicular * radius - camPos);

            previous = current;
            previousRadius = radius;
        }
    }
}
