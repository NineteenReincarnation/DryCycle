using DryCycle.Creatures.Platforming;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>Passive shell and standing rig. All real collision masses belong to the shell.</summary>
public sealed class MantleCrab : Creature, IWalkableDynamicSurface, IDynamicWalkableCurveGeometry
{
    internal static readonly Vector2[] ShellRest =
    [new(-84, 0), new(-44, 5), new(0, 8), new(44, 5), new(84, 0)];
    private static readonly float[] Radii = [17, 26, 30, 26, 17];
    private const int WalkableCurvePointCount = 42;
    private const float VisualShellWidth = 202f;
    private const float VisualBodyStart = 17f;
    private const float VisualBodyEnd = 185f;
    private const float VisualBodyWidth = VisualBodyEnd - VisualBodyStart;
    internal readonly MantleCrabLimb[] Legs = new MantleCrabLimb[4];
    internal readonly MantleCrabLimb[] Pincers = new MantleCrabLimb[2];
    private readonly float[] supportAccelerations = new float[4];

    private Vector2 shellRestCenter;
    private float shellRestInertia;
    private Vector2 shellCenter;
    private Vector2 shellAxis = Vector2.right;
    private bool shellFrameInitialized;

    internal float ShellScale = 1f;
    internal readonly Rendering.MantleCrabVisualPhenotype Phenotype;
    internal int SupportingFeet { get; private set; }
    internal Vector2 Axis
    {
        get
        {
            if (shellFrameInitialized) return shellAxis;
            if (bodyChunks == null || bodyChunks.Length < 5) return Vector2.right;
            Vector2 fallback = bodyChunks[4].pos - bodyChunks[0].pos;
            return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector2.right;
        }
    }

    Room IWalkableDynamicSurface.SurfaceRoom => room;
    bool IWalkableDynamicSurface.SurfaceEnabled => room != null && !slatedForDeletetion;

    bool IWalkableDynamicSurface.TrySample(Vector2 worldPosition, out WalkableSurfaceSample sample) =>
        TrySampleWalkableSurface(worldPosition, out sample);

    bool IWalkableDynamicSurface.TrySample(float coordinate, out WalkableSurfaceSample sample) =>
        TrySampleWalkableSurface(coordinate, out sample);

    int IDynamicWalkableCurveGeometry.CurvePointCount => WalkableCurvePointCount;

    bool IDynamicWalkableCurveGeometry.TryGetCurvePoint(int index, bool previous, out Vector2 point) =>
        TryGetWalkableCurvePoint(index, previous, out point);

    public MantleCrab(AbstractCreature creature, World world) : base(creature, world)
    {
        Phenotype = new Rendering.MantleCrabVisualPhenotype(new Rendering.MantleCrabVisualGenome(creature));
        ShellScale = Phenotype.ShellWidth;
        bodyChunks = new BodyChunk[5];
        for (int i = 0; i < 5; i++)
            bodyChunks[i] = new BodyChunk(this, i, ShellRest[i] * ShellScale, Radii[i] * ShellScale, i == 2 ? 3f : 1.8f);

        // The complete distance graph remains as collision-time bracing, but it is no longer
        // responsible for visible shell stiffness. MaintainRigidShell removes all internal
        // deformation after Creature physics resolves the frame.
        bodyChunkConnections = new BodyChunkConnection[10];
        int connection = 0;
        for (int i = 0; i < 5; i++)
        for (int j = i + 1; j < 5; j++)
            bodyChunkConnections[connection++] = new BodyChunkConnection(bodyChunks[i], bodyChunks[j],
                Vector2.Distance(ShellRest[i], ShellRest[j]) * ShellScale, BodyChunkConnection.Type.Normal, .85f, -1f);

        for (int i = 0; i < 4; i++) Legs[i] = new MantleCrabLimb(i, false);
        for (int i = 0; i < 2; i++) Pincers[i] = new MantleCrabLimb(i, true);
        airFriction = .995f;
        gravity = .9f;
        bounce = .05f;
        surfaceFriction = .45f;
        waterFriction = .96f;
        buoyancy = .8f;
        collisionLayer = 1;
        GoThroughFloors = false;
    }

    public override void PlaceInRoom(Room placeRoom)
    {
        base.PlaceInRoom(placeRoom);
        Vector2 center = placeRoom.MiddleOfTile(abstractCreature.pos.Tile);
        for (int i = 0; i < 5; i++)
        {
            bodyChunks[i].HardSetPosition(center + ShellRest[i] * ShellScale);
            bodyChunks[i].vel = Vector2.zero;
        }

        ResetRigidShellFrame();

        // DevConsole and ordinary realization both enter through PlaceInRoom. If a normal
        // standing surface is already reachable, plant the visual legs immediately so the
        // first gravity frames do not make the tall passive prototype crumple before support
        // engages. A genuinely airborne spawn still falls normally.
        ResetLimbs(snapWalkingToSupport: true);
    }

    public override void NewRoom(Room newRoom)
    {
        base.NewRoom(newRoom);
        ResetRigidShellFrame();
        ResetLimbs(snapWalkingToSupport: false);
    }

    private void ResetLimbs(bool snapWalkingToSupport)
    {
        SupportingFeet = 0;
        foreach (MantleCrabLimb leg in Legs)
        {
            Vector2 anchor = Anchor(leg);
            leg.Reset(anchor);
            if (snapWalkingToSupport && leg.TrySnapToSupport(this, anchor))
                SupportingFeet++;
        }

        foreach (MantleCrabLimb pincer in Pincers)
            pincer.Reset(Anchor(pincer));

        graphicsModule?.Reset();
    }

    internal Vector2 Anchor(MantleCrabLimb limb)
    {
        Vector2 axis = Axis;
        Vector2 up = new(-axis.y, axis.x);
        return bodyChunks[2].pos + axis * limb.Rest[0].x + up * limb.Rest[0].y;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        if (room == null) return;

        // Collision is allowed to move any shell chunk during Creature.Update, but before
        // limbs or graphics observe the frame we remove all internal deformation. The shell
        // keeps the collision's linear and angular response, never its bend/compression modes.
        MaintainRigidShell();

        SupportingFeet = 0;
        foreach (MantleCrabLimb leg in Legs)
        {
            leg.Update(this, Anchor(leg));
            if (leg.Planted) SupportingFeet++;
        }
        foreach (MantleCrabLimb pincer in Pincers) pincer.Update(this, Anchor(pincer));

        if (!Consious || SupportingFeet == 0) return;
        ApplySupport(gravity * room.gravity);

        // Support is applied at individual shell stations to create legitimate torque. Project
        // those impulses back to rigid-body velocities immediately so they cannot seed a new
        // internal vibration for the next frame.
        RigidifyShellVelocities();
    }

    internal void ApplySupport(float effectiveGravity)
    {
        float totalMass = TotalMass;
        Vector2 center = Vector2.zero, velocity = Vector2.zero;
        foreach (BodyChunk chunk in bodyChunks)
        {
            center += chunk.pos * chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }
        center /= totalMass;
        velocity /= totalMass;

        float angularMomentum = 0f, inertia = 0f;
        foreach (BodyChunk chunk in bodyChunks)
        {
            Vector2 offset = chunk.pos - center, relativeVelocity = chunk.vel - velocity;
            angularMomentum += chunk.mass * Cross(offset, relativeVelocity);
            inertia += chunk.mass * offset.sqrMagnitude;
        }
        float angularVelocity = angularMomentum / Mathf.Max(1f, inertia);

        for (int i = 0; i < Legs.Length; i++)
        {
            MantleCrabLimb leg = Legs[i];
            supportAccelerations[i] = 0f;
            if (!leg.Planted) continue;
            BodyChunk anchor = bodyChunks[leg.AnchorChunk];
            float extensionError = leg.StandHeight - (Anchor(leg).y - leg.Tip.y);
            float stationVelocity = velocity.y + angularVelocity * (anchor.pos.x - center.x);
            supportAccelerations[i] = MantleCrabRigMath.SupportAcceleration(extensionError,
                stationVelocity, effectiveGravity, SupportingFeet);
        }

        // Evaluate every leg against the same velocity snapshot. Paired legs share a chunk;
        // applying the first force before evaluating the second would make support order-dependent.
        for (int i = 0; i < Legs.Length; i++)
        {
            MantleCrabLimb leg = Legs[i];
            if (!leg.Planted) continue;
            BodyChunk anchor = bodyChunks[leg.AnchorChunk];
            anchor.vel.y += supportAccelerations[i] * totalMass / anchor.mass;
            if (SupportingFeet >= 2)
                anchor.vel.x -= Mathf.Clamp(anchor.vel.x * .08f, -.35f, .35f);
        }
    }

    /// <summary>
    /// Projects the five collision chunks onto the single rigid shell frame that best fits
    /// their post-collision positions. The projection preserves mass-centre translation and
    /// angular momentum, while deleting every internal stretch, bend and compression mode.
    /// </summary>
    internal void MaintainRigidShell()
    {
        if (bodyChunks == null || bodyChunks.Length != ShellRest.Length) return;
        EnsureRigidShellMetrics();

        float mass = 0f;
        Vector2 center = Vector2.zero;
        Vector2 velocity = Vector2.zero;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            if (chunk == null) return;
            mass += chunk.mass;
            center += chunk.pos * chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }
        if (mass <= 0.0001f) return;
        center /= mass;
        velocity /= mass;

        float fitDot = 0f;
        float fitCross = 0f;
        float angularMomentum = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            Vector2 local = ShellRest[i] * ShellScale - shellRestCenter;
            Vector2 current = chunk.pos - center;
            fitDot += chunk.mass * Vector2.Dot(local, current);
            fitCross += chunk.mass * Cross(local, current);

            Vector2 relativeVelocity = chunk.vel - velocity;
            angularMomentum += chunk.mass * Cross(current, relativeVelocity);
        }

        Vector2 axis;
        if (fitDot * fitDot + fitCross * fitCross > 0.0001f)
        {
            float angle = Mathf.Atan2(fitCross, fitDot);
            axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
        else
        {
            axis = shellFrameInitialized ? shellAxis : Vector2.right;
        }
        Vector2 up = new(-axis.y, axis.x);
        float angularVelocity = angularMomentum / Mathf.Max(1f, shellRestInertia);

        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            Vector2 local = ShellRest[i] * ShellScale - shellRestCenter;
            Vector2 offset = axis * local.x + up * local.y;

            // Intentionally assign pos rather than HardSetPosition: lastPos remains the previous
            // rigid frame, so normal Rain World render interpolation stays smooth.
            chunk.pos = center + offset;
            chunk.vel = velocity + new Vector2(-offset.y, offset.x) * angularVelocity;
        }

        shellCenter = center;
        shellAxis = axis;
        shellFrameInitialized = true;
    }

    private void RigidifyShellVelocities()
    {
        if (bodyChunks == null || bodyChunks.Length != ShellRest.Length) return;
        EnsureRigidShellMetrics();

        float mass = 0f;
        Vector2 center = Vector2.zero;
        Vector2 velocity = Vector2.zero;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            if (chunk == null) return;
            mass += chunk.mass;
            center += chunk.pos * chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }
        if (mass <= 0.0001f) return;
        center /= mass;
        velocity /= mass;

        float angularMomentum = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            Vector2 offset = chunk.pos - center;
            angularMomentum += chunk.mass * Cross(offset, chunk.vel - velocity);
        }
        float angularVelocity = angularMomentum / Mathf.Max(1f, shellRestInertia);

        for (int i = 0; i < bodyChunks.Length; i++)
        {
            Vector2 offset = bodyChunks[i].pos - center;
            bodyChunks[i].vel = velocity + new Vector2(-offset.y, offset.x) * angularVelocity;
        }

        shellCenter = center;
    }

    private void ResetRigidShellFrame()
    {
        shellFrameInitialized = false;
        shellRestInertia = 0f;
        EnsureRigidShellMetrics();
        MaintainRigidShell();

        // Spawn/room transfer should not inherit an artificial interpolation bend. The current
        // chunk positions are already the canonical rigid shape here.
        for (int i = 0; i < bodyChunks.Length; i++)
            bodyChunks[i].lastPos = bodyChunks[i].pos;
    }

    private void EnsureRigidShellMetrics()
    {
        if (shellRestInertia > 0.0001f) return;
        if (bodyChunks == null || bodyChunks.Length != ShellRest.Length) return;

        float mass = 0f;
        shellRestCenter = Vector2.zero;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            if (chunk == null) return;
            mass += chunk.mass;
            shellRestCenter += ShellRest[i] * ShellScale * chunk.mass;
        }
        if (mass <= 0.0001f) return;
        shellRestCenter /= mass;

        shellRestInertia = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            Vector2 local = ShellRest[i] * ShellScale - shellRestCenter;
            shellRestInertia += bodyChunks[i].mass * local.sqrMagnitude;
        }
    }

    /// <summary>
    /// Finite top-shell curve used by the shared moving-surface runtime. The provider exposes only
    /// geometry; nearest-segment projection, endpoint roll-off and normalized rider coordinates are
    /// owned by DynamicWalkableCurveSampler so future creatures can reuse the same collision model.
    /// The curve follows the broad visible mantle crown all the way to the rendered shell tips.
    /// </summary>
    internal bool TrySampleWalkableSurface(Vector2 worldPosition, out WalkableSurfaceSample sample) =>
        DynamicWalkableCurveSampler.TrySample(this, this, worldPosition, out sample);

    internal bool TrySampleWalkableSurface(float coordinate, out WalkableSurfaceSample sample) =>
        DynamicWalkableCurveSampler.TrySample(this, this, coordinate, out sample);

    private bool TryGetWalkableCurvePoint(int index, bool previous, out Vector2 point)
    {
        point = default;
        if (index < 0 || index >= WalkableCurvePointCount || bodyChunks == null || bodyChunks.Length != 5)
            return false;

        float u = index / (WalkableCurvePointCount - 1f);
        return TryGetSmoothShellTopPoint(u, previous, out point);
    }

    /// <summary>
    /// Broad collision envelope shared conceptually with MantleCrabGraphics.DrawShell. Fine edge
    /// noise and the tiny crown teeth remain visual-only so they cannot make a standing BodyChunk
    /// jitter, while crown, wing droop, phenotype tilt and large asymmetry remain physical.
    /// </summary>
    private bool TryGetSmoothShellTopPoint(float u, bool previous, out Vector2 point)
    {
        point = default;
        if (!TryFitShellFrame(previous, out _, out Vector2 axis)) return false;

        Vector2 up = new(-axis.y, axis.x);
        float shellX = Mathf.Clamp01(u) * VisualShellWidth;
        float bodyU = Mathf.Clamp01((shellX - VisualBodyStart) / VisualBodyWidth) * 4f;
        int station = Mathf.Min(3, Mathf.FloorToInt(bodyU));
        float stationT = bodyU - station;

        Vector2 a = previous ? bodyChunks[station].lastPos : bodyChunks[station].pos;
        Vector2 b = previous ? bodyChunks[station + 1].lastPos : bodyChunks[station + 1].pos;
        Vector2 center = Vector2.Lerp(a, b, stationT);

        if (u < VisualBodyStart / VisualShellWidth)
            center += axis * ((shellX - VisualBodyStart) * ShellScale);
        if (u > VisualBodyEnd / VisualShellWidth)
            center += axis * ((shellX - VisualBodyEnd) * ShellScale);

        float signed = u * 2f - 1f;
        float wing = Mathf.Abs(signed);
        float crownBase = Mathf.Max(0f, 1f - Mathf.Pow(wing / .82f, 2f));
        float crown = Mathf.Pow(crownBase, 1.25f);
        float wingAngle = Phenotype == null ? 0f : Phenotype.WingAngle;
        float asymmetry = Phenotype == null ? 0f : Phenotype.Asymmetry;
        float largeAsymmetry = signed * asymmetry * 18f;

        center += up * (-5.5f * Mathf.Pow(wing, 1.7f) + wingAngle * 70f + largeAsymmetry);
        center += up * (8f - Mathf.Lerp(ShellRest[station].y, ShellRest[station + 1].y, stationT));

        float top = 5.5f + 30f * crown;
        point = center + up * top;
        return true;
    }

    private bool TryFitShellFrame(bool previous, out Vector2 center, out Vector2 axis)
    {
        center = Vector2.zero;
        axis = Vector2.right;
        if (bodyChunks == null || bodyChunks.Length != ShellRest.Length) return false;
        EnsureRigidShellMetrics();

        float mass = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            if (chunk == null) return false;
            Vector2 position = previous ? chunk.lastPos : chunk.pos;
            mass += chunk.mass;
            center += position * chunk.mass;
        }
        if (mass <= 0.0001f) return false;
        center /= mass;

        float fitDot = 0f;
        float fitCross = 0f;
        for (int i = 0; i < bodyChunks.Length; i++)
        {
            BodyChunk chunk = bodyChunks[i];
            Vector2 local = ShellRest[i] * ShellScale - shellRestCenter;
            Vector2 position = previous ? chunk.lastPos : chunk.pos;
            Vector2 current = position - center;
            fitDot += chunk.mass * Vector2.Dot(local, current);
            fitCross += chunk.mass * Cross(local, current);
        }

        if (fitDot * fitDot + fitCross * fitCross <= 0.0001f)
        {
            axis = shellFrameInitialized ? shellAxis : Vector2.right;
            return true;
        }

        float angle = Mathf.Atan2(fitCross, fitDot);
        axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    public override void InitiateGraphicsModule()
    {
        graphicsModule ??= new MantleCrabGraphics(this);
    }
}
