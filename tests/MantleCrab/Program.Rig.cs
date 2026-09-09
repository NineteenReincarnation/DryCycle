using System;
using System.Collections.Generic;
using DryCycle.Creatures.MantleCrab;
using UnityEngine;

internal static partial class Program
{
    private readonly struct StandingResult
    {
        internal readonly float FinalHeight;
        internal readonly float LateOscillation;

        internal StandingResult(float finalHeight, float lateOscillation)
        {
            FinalHeight = finalHeight;
            LateOscillation = lateOscillation;
        }
    }

    private static void RigTests()
    {
        AnatomyContractTests();
        IkTests();
        SupportMathTests();
        FrameTests();
    }

    private static void AnatomyContractTests()
    {
        Check(MantleCrabAnatomy.Walking.Length == 4, "MantleCrab must have four walking-leg anatomy chains");
        Check(MantleCrabAnatomy.Claws.Length == 2, "MantleCrab must have two pincer anatomy chains");

        for (int i = 0; i < MantleCrabAnatomy.Walking.Length; i++)
        {
            Vector2[] chain = MantleCrabAnatomy.Walking[i];
            Check(chain.Length == 5, "Walking leg " + i + " must contain root/proximal/knee/ankle/tip landmarks");
            float stance = chain[0].y - chain[4].y;
            Check(stance >= 260f && stance <= 320f, "Walking leg " + i + " escaped V2 standing-height bounds");
            Check(Math.Abs(chain[2].x - chain[1].x) >= 8f, "Walking leg " + i + " lost its visible knee offset");
            Check(HasArticulation(chain), "Walking leg " + i + " collapsed into a straight rod");
        }

        Check(!SimpleMirror(MantleCrabAnatomy.Walking[0], MantleCrabAnatomy.Walking[1]),
            "Outer walking legs regressed to exact mirror copies");
        Check(!SimpleMirror(MantleCrabAnatomy.Walking[2], MantleCrabAnatomy.Walking[3]),
            "Inner walking legs regressed to exact mirror copies");

        for (int i = 0; i < MantleCrabAnatomy.Claws.Length; i++)
        {
            Vector2[] chain = MantleCrabAnatomy.Claws[i];
            Check(chain.Length == 5, "Pincer " + i + " must contain root/upper/elbow/wrist/tip landmarks");
            Check(HasArticulation(chain), "Pincer " + i + " regressed to a straight hanging spear");
        }
    }

    private static bool HasArticulation(Vector2[] chain)
    {
        for (int i = 0; i < chain.Length - 2; i++)
        {
            Vector2 a = (chain[i + 1] - chain[i]).normalized;
            Vector2 b = (chain[i + 2] - chain[i + 1]).normalized;
            float cross = Math.Abs(a.x * b.y - a.y * b.x);
            if (cross > .08f)
                return true;
        }
        return false;
    }

    private static bool SimpleMirror(Vector2[] left, Vector2[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (Math.Abs(left[i].x + right[i].x) > .01f || Math.Abs(left[i].y - right[i].y) > .01f)
                return false;
        }
        return true;
    }

    private static void IkTests()
    {
        float maxTipError = 0f;
        for (int i = 0; i < 4; i++)
            maxTipError = Math.Max(maxTipError, RunLimbPoseSet(i, false, 200));
        for (int i = 0; i < 2; i++)
            maxTipError = Math.Max(maxTipError, RunLimbPoseSet(i, true, 150));

        Console.WriteLine("Worst V2 anatomy IK tip error: " + maxTipError);
    }

    private static float RunLimbPoseSet(int index, bool pincer, int poses)
    {
        MantleCrabLimb limb = new(index, pincer);
        Vector2 anchor = new(0f, 320f);
        System.Random random = new(9100 + index * 137 + (pincer ? 1000 : 0));
        float maxError = 0f;

        for (int pose = 0; pose < poses; pose++)
        {
            Vector2 target = anchor + limb.RestTipOffset + new Vector2(
                (float)(random.NextDouble() * 70d - 35d),
                (float)(random.NextDouble() * 55d - 27.5d));

            Vector2 fromAnchor = target - anchor;
            float maxReach = limb.Reach * .90f;
            if (fromAnchor.magnitude > maxReach)
                target = anchor + fromAnchor.normalized * maxReach;

            limb.Reset(anchor);
            limb.GroundNormal = Vector2.up;
            limb.SolvePose(anchor, target, !pincer);

            Vector2 previous = anchor;
            bool chainValid = true;
            float maxLengthDrift = 0f;
            for (int segment = 0; segment < 4; segment++)
            {
                Vector2 point = limb.Pos[segment];
                chainValid &= Finite(point);
                float drift = Math.Abs(Vector2.Distance(previous, point) - limb.Lengths[segment]);
                maxLengthDrift = Math.Max(maxLengthDrift, drift);
                chainValid &= drift < .01f;
                previous = point;
            }
            Check(chainValid,
                "V2 anatomy chain invalid: limb=" + index + " pose=" + pose + " maxLengthDrift=" + maxLengthDrift);

            float error = Vector2.Distance(limb.Tip, target);
            maxError = Math.Max(maxError, error);
            Check(error < 1.2f, "Reachable V2 limb failed target contact: limb=" + index + " error=" + error);

            if (pincer)
                pincerIkPoses++;
            else
                walkingIkPoses++;
        }

        return maxError;
    }

    private static void SupportMathTests()
    {
        Check(MantleCrabRigMath.SupportAcceleration(0f, 0f, .9f, 0) == 0f, "Zero feet must produce zero support");

        float height = 240f;
        float velocity = 0f;
        for (int tick = 0; tick < 600; tick++)
        {
            velocity -= .9f;
            height += velocity;
            velocity += MantleCrabRigMath.SupportAcceleration(280f - height, velocity, .9f, 1);
        }
        Check(height < 0f, "One foot must not indefinitely suspend the full shell");

        float[] errors = { -100f, 0f, 100f };
        float[] speeds = { -20f, 0f, 20f };
        for (int feet = 2; feet <= 4; feet++)
        foreach (float error in errors)
        foreach (float speed in speeds)
        {
            float acceleration = MantleCrabRigMath.SupportAcceleration(error, speed, .9f, feet);
            Check(Finite(acceleration), "SupportAcceleration returned a non-finite value");
            Check(acceleration >= 0f && acceleration <= .9f * 1.8f / feet + .0001f,
                "SupportAcceleration escaped configured capacity for feet=" + feet);
        }
    }

    private static void FrameTests()
    {
        Vector2[] rest = MantleCrab.ShellRest;
        BodyChunk[] chunks = new BodyChunk[5];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = Empty<BodyChunk>();
            chunks[i].mass = i == 2 ? 3f : 1.8f;
            chunks[i].pos = rest[i];
            chunks[i].lastPos = rest[i];
            chunks[i].vel = Vector2.zero;
        }

        List<PhysicalObject.BodyChunkConnection> constraints = new();
        for (int i = 0; i < chunks.Length; i++)
        for (int j = i + 1; j < chunks.Length; j++)
        {
            constraints.Add(new PhysicalObject.BodyChunkConnection(
                chunks[i],
                chunks[j],
                Vector2.Distance(rest[i], rest[j]),
                PhysicalObject.BodyChunkConnection.Type.Normal,
                .85f,
                -1f));
        }

        chunks[0].vel = new Vector2(8f, 12f);
        for (int tick = 0; tick < 600; tick++)
        {
            foreach (BodyChunk chunk in chunks)
            {
                chunk.vel *= .96f;
                chunk.pos += chunk.vel;
            }
            foreach (PhysicalObject.BodyChunkConnection connection in constraints)
                connection.Update();
        }

        foreach (PhysicalObject.BodyChunkConnection connection in constraints)
        {
            Check(Math.Abs(Vector2.Distance(connection.chunk1.pos, connection.chunk2.pos) - connection.distance) < .5f,
                "Braced shell collapsed after impulse");
        }
        Check(Vector2.Distance(chunks[0].pos, chunks[4].pos) > 165f, "Shell lost span after impulse");

        MantleCrab crab = Empty<MantleCrab>();
        crab.bodyChunks = chunks;
        crab.ShellScale = 1f;
        SetField(crab, "supportAccelerations", new float[4]);

        MantleCrabLimb[] legs = new MantleCrabLimb[4];
        for (int i = 0; i < legs.Length; i++)
        {
            legs[i] = new MantleCrabLimb(i, false);
            SetField(legs[i], "<Planted>k__BackingField", true);
            legs[i].Pos[3] = Vector2.zero;
        }
        SetField(crab, "Legs", legs);

        RigidProjectionContract(crab, chunks, rest);

        for (int feet = 4; feet >= 2; feet--)
        {
            StandingResult result = RunStandingSimulation(crab, chunks, constraints, rest, legs, feet);
            Check(result.FinalHeight > 265f && result.FinalHeight < 305f,
                "Supported shell failed to reach stance with " + feet + " feet; height=" + result.FinalHeight);
            Check(result.LateOscillation < 2f,
                "Supported rigid frame sustained oscillation with " + feet + " feet; amplitude=" + result.LateOscillation);
            standingCases++;
        }
    }

    private static void RigidProjectionContract(MantleCrab crab, BodyChunk[] chunks, Vector2[] rest)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].pos = rest[i] + new Vector2(130f, 90f);
            chunks[i].vel = new Vector2(2f, -1f);
        }

        // Inject the exact deformation that used to produce the visible jelly response:
        // independent shell stations are displaced and given conflicting velocities.
        chunks[0].pos += new Vector2(8f, 19f);
        chunks[2].pos += new Vector2(-3f, -14f);
        chunks[4].pos += new Vector2(-11f, 23f);
        chunks[0].vel += new Vector2(5f, 3f);
        chunks[4].vel += new Vector2(-4f, -2f);

        Vector2 momentumBefore = LinearMomentum(chunks);
        crab.MaintainRigidShell();
        Vector2 momentumAfter = LinearMomentum(chunks);

        float maxDistanceError = 0f;
        for (int i = 0; i < chunks.Length; i++)
        for (int j = i + 1; j < chunks.Length; j++)
        {
            float expected = Vector2.Distance(rest[i], rest[j]);
            float actual = Vector2.Distance(chunks[i].pos, chunks[j].pos);
            maxDistanceError = Math.Max(maxDistanceError, Math.Abs(actual - expected));
        }

        Check(maxDistanceError < .01f,
            "Rigid shell projection left visible internal deformation; max distance error=" + maxDistanceError);
        Check(Vector2.Distance(momentumBefore, momentumAfter) < .01f,
            "Rigid shell projection failed to preserve linear momentum");
        Check(Finite(crab.Axis) && Math.Abs(crab.Axis.magnitude - 1f) < .001f,
            "Rigid shell frame produced an invalid orientation axis");
    }

    private static Vector2 LinearMomentum(BodyChunk[] chunks)
    {
        Vector2 momentum = Vector2.zero;
        foreach (BodyChunk chunk in chunks)
            momentum += chunk.vel * chunk.mass;
        return momentum;
    }

    private static StandingResult RunStandingSimulation(
        MantleCrab crab,
        BodyChunk[] chunks,
        List<PhysicalObject.BodyChunkConnection> constraints,
        Vector2[] rest,
        MantleCrabLimb[] legs,
        int plantedFeet)
    {
        for (int i = 0; i < legs.Length; i++)
            SetField(legs[i], "<Planted>k__BackingField", i < plantedFeet);
        SetField(crab, "<SupportingFeet>k__BackingField", plantedFeet);

        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].pos = rest[i] + new Vector2(0f, 240f);
            chunks[i].lastPos = chunks[i].pos;
            chunks[i].vel = Vector2.zero;
        }

        float low = float.MaxValue;
        float high = float.MinValue;
        for (int tick = 0; tick < 1200; tick++)
        {
            foreach (BodyChunk chunk in chunks)
            {
                chunk.vel *= .995f;
                chunk.vel.y -= .9f;
                chunk.pos += chunk.vel;
            }

            foreach (PhysicalObject.BodyChunkConnection connection in constraints)
                connection.Update();

            crab.MaintainRigidShell();
            crab.ApplySupport(.9f);
            // Match production Update(): support impulses are immediately collapsed back into
            // rigid translation/rotation instead of becoming internal shell vibration.
            crab.MaintainRigidShell();

            if (tick > 1000)
            {
                low = Math.Min(low, chunks[2].pos.y);
                high = Math.Max(high, chunks[2].pos.y);
            }
        }

        return new StandingResult(chunks[2].pos.y, high - low);
    }
}
