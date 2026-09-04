using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class RopeSpear : Spear
{
    private const int RopeMeshSegments = 28;
    private const float RopeThickness = 1.15f;
    private const float ReelSpeed = 2.15f;
    private const float TensionSpring = 0.032f;
    private const float TensionDamping = 0.06f;
    private const float MaxPlayerPullImpulse = 2.4f;
    private const float MaxAnchorPullImpulse = 1.8f;
    private const float BreakStretch = 92f;
    private const int BreakFramesRequired = 8;

    private readonly List<Vector2> _drawPath = new();
    private readonly Vector2[] _sampledRope = new Vector2[RopeMeshSegments + 1];

    private Player _ropeOwner;
    private Rope _ropeTopology;
    private Room _ropeRoom;
    private int _ropeSpriteIndex = -1;
    private int _breakFrames;
    private float _lastRouteLength;
    private bool _ropeDeployed;

    public RopeSpear(AbstractPhysicalObject abstractPhysicalObject, World world)
        : base(abstractPhysicalObject, world)
    {
    }

    private AbstractRopeSpear Data => abstractPhysicalObject as AbstractRopeSpear;

    internal bool RopeActive =>
        _ropeDeployed &&
        !IsBroken &&
        _ropeOwner != null &&
        room != null &&
        _ropeOwner.room == room;

    internal Player RopeOwner => _ropeOwner;

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

    public override void Thrown(
        Creature thrownBy,
        Vector2 thrownPos,
        Vector2? firstFrameTraceFromPos,
        IntVector2 throwDir,
        float frc,
        bool eu)
    {
        base.Thrown(thrownBy, thrownPos, firstFrameTraceFromPos, throwDir, frc, eu);

        if (thrownBy is Player player && !IsBroken)
        {
            _ropeOwner = player;
            _ropeDeployed = true;
            _breakFrames = 0;
            EnsureTopology(forceReset: true);
        }
    }

    public override void PickedUp(Creature upPicker)
    {
        base.PickedUp(upPicker);

        if (upPicker is Player player)
        {
            _ropeOwner = player;
        }

        RetractRope(resetLength: true);
    }

    public override void Update(bool eu)
    {
        base.Update(eu);

        if (!_ropeDeployed || IsBroken)
        {
            return;
        }

        if (!OwnerCanHoldRope())
        {
            DisconnectRope();
            return;
        }

        EnsureTopology(forceReset: false);
        if (_ropeTopology == null)
        {
            return;
        }

        HandleReelingInput();

        Vector2 playerPoint = GetPlayerRopePoint(_ropeOwner, 1f);
        Vector2 spearPoint = GetSpearRopePoint(1f);
        _ropeTopology.Update(playerPoint, spearPoint);

        float routeLength = _ropeTopology.totalLength;
        float stretch = routeLength - RopeLength;
        if (stretch > 0f)
        {
            ApplyRopeConstraint(stretch, routeLength);
        }

        if (stretch >= BreakStretch)
        {
            _breakFrames++;
            if (_breakFrames >= BreakFramesRequired)
            {
                BreakRope();
                return;
            }
        }
        else
        {
            _breakFrames = Mathf.Max(0, _breakFrames - 2);
        }

        _lastRouteLength = routeLength;
    }

    private bool OwnerCanHoldRope()
    {
        return _ropeOwner != null &&
               !_ropeOwner.slatedForDeletetion &&
               !_ropeOwner.dead &&
               _ropeOwner.room == room &&
               !_ropeOwner.inShortcut &&
               room != null;
    }

    private void HandleReelingInput()
    {
        if (_ropeOwner?.input == null || _ropeOwner.input.Length == 0)
        {
            return;
        }

        Player.InputPackage input = _ropeOwner.input[0];
        if (!input.pckp)
        {
            return;
        }

        if (input.y > 0)
        {
            RopeLength -= ReelSpeed;
        }
        else if (input.y < 0)
        {
            RopeLength += ReelSpeed;
        }
    }

    private void ApplyRopeConstraint(float stretch, float routeLength)
    {
        if (_ropeTopology == null || _ropeOwner == null || routeLength <= 0.001f)
        {
            return;
        }

        BodyChunk playerChunk = _ropeOwner.mainBodyChunk;
        BodyChunk anchorChunk = GetMovableAnchorChunk();

        Vector2 playerDir = Custom.DirVec(playerChunk.pos, _ropeTopology.AConnect);
        Vector2 anchorPos = anchorChunk?.pos ?? firstChunk.pos;
        Vector2 anchorDir = Custom.DirVec(anchorPos, _ropeTopology.BConnect);

        float routeSpeed = routeLength - _lastRouteLength;
        float springImpulse = stretch * TensionSpring + Mathf.Max(0f, routeSpeed) * TensionDamping;
        float playerImpulse = Mathf.Min(MaxPlayerPullImpulse, springImpulse);

        playerChunk.vel += playerDir * playerImpulse;
        if (_ropeOwner.bodyChunks != null && _ropeOwner.bodyChunks.Length > 1)
        {
            _ropeOwner.bodyChunks[1].vel += playerDir * playerImpulse * 0.72f;
        }

        if (anchorChunk != null)
        {
            float anchorMassScale = Mathf.Clamp01(0.12f / Mathf.Max(0.05f, anchorChunk.mass));
            float anchorImpulse = Mathf.Min(MaxAnchorPullImpulse, springImpulse * anchorMassScale);
            anchorChunk.vel += anchorDir * anchorImpulse;
        }
    }

    private BodyChunk GetMovableAnchorChunk()
    {
        if (mode == Mode.StuckInWall)
        {
            return null;
        }

        if (mode == Mode.StuckInCreature && stuckInObject != null)
        {
            try
            {
                return stuckInChunk;
            }
            catch
            {
                return null;
            }
        }

        return firstChunk;
    }

    private void EnsureTopology(bool forceReset)
    {
        if (room == null || _ropeOwner == null || _ropeOwner.room != room)
        {
            _ropeTopology = null;
            _ropeRoom = null;
            return;
        }

        Vector2 playerPoint = GetPlayerRopePoint(_ropeOwner, 1f);
        Vector2 spearPoint = GetSpearRopePoint(1f);

        if (forceReset || _ropeTopology == null || _ropeRoom != room)
        {
            _ropeTopology = new Rope(room, playerPoint, spearPoint, RopeThickness);
            _ropeRoom = room;
            _lastRouteLength = Vector2.Distance(playerPoint, spearPoint);
        }
    }

    private void BreakRope()
    {
        if (Data != null)
        {
            Data.RopeBroken = true;
        }

        _ropeDeployed = false;
        _breakFrames = 0;
        _ropeTopology?.Reset();
        _ropeTopology = null;
        _ropeRoom = null;
        _ropeOwner = null;

        if (room != null)
        {
            firstChunk.vel += Custom.RNV() * 1.2f;
        }
    }

    private void DisconnectRope()
    {
        _ropeDeployed = false;
        _breakFrames = 0;
        _ropeTopology?.Reset();
        _ropeTopology = null;
        _ropeRoom = null;
        _ropeOwner = null;
    }

    private void RetractRope(bool resetLength)
    {
        _ropeDeployed = false;
        _breakFrames = 0;
        _ropeTopology?.Reset();
        _ropeTopology = null;
        _ropeRoom = null;

        if (resetLength && Data != null && !Data.RopeBroken)
        {
            Data.RopeLength = AbstractRopeSpear.DefaultRopeLength;
        }
    }

    private static Vector2 GetPlayerRopePoint(Player player, float timeStacker)
    {
        if (player?.bodyChunks == null || player.bodyChunks.Length == 0)
        {
            return Vector2.zero;
        }

        BodyChunk chunk = player.bodyChunks.Length > 1
            ? player.bodyChunks[1]
            : player.mainBodyChunk;
        return Vector2.Lerp(chunk.lastPos, chunk.pos, timeStacker);
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

        bool visible = RopeActive && _ropeTopology != null;
        mesh.isVisible = visible;
        if (!visible)
        {
            return;
        }

        BuildDrawPath(timeStacker);
        if (_drawPath.Count < 2)
        {
            mesh.isVisible = false;
            return;
        }

        SampleDrawPath(_drawPath, RopeLength, _sampledRope);
        DrawRopeMesh(mesh, _sampledRope, camPos);
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

    private void BuildDrawPath(float timeStacker)
    {
        _drawPath.Clear();
        if (_ropeOwner == null || _ropeTopology == null)
        {
            return;
        }

        _drawPath.Add(GetPlayerRopePoint(_ropeOwner, timeStacker));
        for (int i = 0; i < _ropeTopology.bends.Count; i++)
        {
            _drawPath.Add(_ropeTopology.bends[i].pos);
        }
        _drawPath.Add(GetSpearRopePoint(timeStacker));
    }

    private static void SampleDrawPath(
        List<Vector2> path,
        float restLength,
        Vector2[] output)
    {
        float tautLength = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            tautLength += Vector2.Distance(path[i - 1], path[i]);
        }

        if (tautLength <= 0.001f)
        {
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = path[0];
            }
            return;
        }

        float slack = Mathf.Clamp(restLength - tautLength, 0f, 70f);

        for (int sample = 0; sample < output.Length; sample++)
        {
            float targetDistance = tautLength * sample / (output.Length - 1f);
            float walked = 0f;

            for (int segment = 1; segment < path.Count; segment++)
            {
                Vector2 a = path[segment - 1];
                Vector2 b = path[segment];
                float length = Vector2.Distance(a, b);
                if (segment != path.Count - 1 && walked + length < targetDistance)
                {
                    walked += length;
                    continue;
                }

                float localT = length <= 0.001f
                    ? 0f
                    : Mathf.Clamp01((targetDistance - walked) / length);
                Vector2 point = Vector2.Lerp(a, b, localT);

                if (slack > 0f && path.Count == 2)
                {
                    float globalT = targetDistance / tautLength;
                    point.y -= Mathf.Sin(globalT * Mathf.PI) * slack * 0.34f;
                }
                else if (slack > 0f)
                {
                    point.y -= Mathf.Sin(localT * Mathf.PI) * slack * 0.12f;
                }

                output[sample] = point;
                break;
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
