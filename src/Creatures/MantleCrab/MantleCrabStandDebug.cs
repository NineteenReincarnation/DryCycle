using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

/// <summary>
/// MantleCrab 站立问题专用诊断日志。
/// 这不是长期游戏系统：它只负责把“身体为什么塌下来”需要的关键状态写进独立文件。
///
/// Dedicated standing diagnostic log for MantleCrab. This is intentionally separate from the normal
/// BepInEx log so a short reproduction can be sent back without unrelated mod noise.
/// </summary>
internal static class MantleCrabStandDebug
{
    private const string FileName = "MantleCrabStandDebug.log";

    private static readonly object Sync = new();
    private static StreamWriter writer;
    private static bool failed;

    internal static string LogPath => Path.Combine(Paths.BepInExRootPath, FileName);

    internal static void RecordPlanning(MantleCrab crab, MantleCrabLocomotion locomotion)
    {
        if (crab == null || locomotion == null)
            return;

        WriteSnapshot(
            crab,
            locomotion,
            "PLAN",
            null,
            null,
            null);
    }

    internal static void RecordGroundForces(
        MantleCrab crab,
        MantleCrabLocomotion locomotion,
        Vector2 beforeSupport,
        Vector2 afterSupport,
        Vector2 afterVerticalStabilizer)
    {
        if (crab == null || locomotion == null)
            return;

        WriteSnapshot(
            crab,
            locomotion,
            "GROUND",
            beforeSupport,
            afterSupport,
            afterVerticalStabilizer);
    }

    private static void WriteSnapshot(
        MantleCrab crab,
        MantleCrabLocomotion locomotion,
        string stage,
        Vector2? beforeSupport,
        Vector2? afterSupport,
        Vector2? afterVerticalStabilizer)
    {
        EnsureWriter();
        if (writer == null)
            return;

        try
        {
            Vector2 center = BodyCenter(crab, out Vector2 bodyVelocity);
            Vector2 axis = crab.Axis;
            float shellAngle = axis.sqrMagnitude > .0001f
                ? Mathf.Atan2(axis.y, axis.x) * Mathf.Rad2Deg
                : 0f;

            int grounded = 0;
            int planted = 0;
            int swinging = 0;
            StringBuilder legs = new();

            for (int i = 0; i < crab.Legs.Length; i++)
            {
                MantleCrabLimb leg = crab.Legs[i];
                bool legGrounded = locomotion.SupportLoad(leg) > 0f;
                bool contactSupported = crab.room != null &&
                                        MantleCrabTerrainProbe.StillSupported(crab.room, leg.Contact);
                bool tipSupported = crab.room != null &&
                                    MantleCrabTerrainProbe.StillSupported(crab.room, leg.Tip);

                if (legGrounded) grounded++;
                if (leg.Planted) planted++;
                if (leg.Swinging) swinging++;

                Vector2 anchor = crab.Anchor(leg);
                float stretch = Vector2.Distance(anchor, leg.Contact) / Mathf.Max(1f, leg.Reach);
                float tipError = Vector2.Distance(leg.Tip, leg.Contact);

                if (i > 0) legs.Append(" | ");
                legs.Append('L').Append(i)
                    .Append("{P=").Append(B(leg.Planted))
                    .Append(",G=").Append(B(legGrounded))
                    .Append(",S=").Append(B(leg.Swinging))
                    .Append(",CS=").Append(B(contactSupported))
                    .Append(",TS=").Append(B(tipSupported))
                    .Append(",phase=").Append(leg.SwingPhase)
                    .Append(",stretch=").Append(F(stretch))
                    .Append(",tipErr=").Append(F(tipError))
                    .Append(",A=").Append(V(anchor))
                    .Append(",C=").Append(V(leg.Contact))
                    .Append(",T=").Append(V(leg.Tip))
                    .Append('}');
            }

            string roomName = crab.room?.abstractRoom?.name ?? "<no-room>";
            float roomGravity = crab.room != null ? crab.room.gravity : float.NaN;

            StringBuilder line = new();
            line.Append("frame=").Append(Time.frameCount)
                .Append(" stage=").Append(stage)
                .Append(" room=").Append(roomName)
                .Append(" conscious=").Append(B(crab.Consious))
                .Append(" supportingFeetField=").Append(crab.SupportingFeet)
                .Append(" grounded=").Append(grounded)
                .Append(" planted=").Append(planted)
                .Append(" swinging=").Append(swinging)
                .Append(" recovering=").Append(B(locomotion.Posture.Recovering))
                .Append(" recoveryPhase=").Append(locomotion.Posture.RecoveryPhase)
                .Append(" shellAngleDeg=").Append(F(shellAngle))
                .Append(" bodyY=").Append(F(center.y))
                .Append(" bodyVel=").Append(V(bodyVelocity))
                .Append(" crabGravity=").Append(F(crab.gravity))
                .Append(" roomGravity=").Append(F(roomGravity))
                .Append(" move=").Append(F(locomotion.SmoothedMoveIntent));

            if (beforeSupport.HasValue)
            {
                line.Append(" beforeSupportVel=").Append(V(beforeSupport.Value))
                    .Append(" afterSupportVel=").Append(V(afterSupport ?? Vector2.zero))
                    .Append(" afterVerticalStabilizerVel=").Append(V(afterVerticalStabilizer ?? Vector2.zero))
                    .Append(" supportDeltaY=").Append(F((afterSupport ?? Vector2.zero).y - beforeSupport.Value.y));
            }

            line.Append(" :: ").Append(legs);

            lock (Sync)
            {
                writer.WriteLine(line.ToString());
            }
        }
        catch (Exception ex)
        {
            DisableAfterFailure(ex);
        }
    }

    private static Vector2 BodyCenter(MantleCrab crab, out Vector2 velocity)
    {
        float totalMass = 0f;
        Vector2 center = Vector2.zero;
        velocity = Vector2.zero;

        if (crab?.bodyChunks == null)
            return Vector2.zero;

        for (int i = 0; i < crab.bodyChunks.Length; i++)
        {
            BodyChunk chunk = crab.bodyChunks[i];
            if (chunk == null) continue;
            totalMass += chunk.mass;
            center += chunk.pos * chunk.mass;
            velocity += chunk.vel * chunk.mass;
        }

        if (totalMass <= .0001f)
            return Vector2.zero;

        velocity /= totalMass;
        return center / totalMass;
    }

    private static void EnsureWriter()
    {
        if (writer != null || failed)
            return;

        lock (Sync)
        {
            if (writer != null || failed)
                return;

            try
            {
                Directory.CreateDirectory(Paths.BepInExRootPath);
                writer = new StreamWriter(
                    new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                writer.WriteLine("MantleCrab standing diagnostic log");
                writer.WriteLine("PLAN appears every creature frame before walking-leg updates. GROUND appears only when MantleCrab.Update actually calls ApplyGroundForces.");
                writer.WriteLine("If a frame has PLAN but no GROUND, the creature skipped its standing-force pass that frame (for example SupportingFeet == 0).");
                writer.WriteLine("P=Planted, G=body considers foot grounded, S=Swinging, CS=stored Contact is on terrain, TS=actual Tip is on terrain.");
                writer.WriteLine();

                global::DryCycle.Plugin.Logger?.LogWarning($"MantleCrab standing debug log: {LogPath}");
            }
            catch (Exception ex)
            {
                failed = true;
                global::DryCycle.Plugin.Logger?.LogError($"Failed to create MantleCrab standing debug log at {LogPath}: {ex}");
            }
        }
    }

    private static void DisableAfterFailure(Exception ex)
    {
        failed = true;
        try
        {
            writer?.Dispose();
        }
        catch
        {
        }
        writer = null;
        global::DryCycle.Plugin.Logger?.LogError($"MantleCrab standing debug logging disabled after write failure: {ex}");
    }

    private static string F(float value) =>
        float.IsNaN(value)
            ? "NaN"
            : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string V(Vector2 value) => $"({F(value.x)},{F(value.y)})";

    private static int B(bool value) => value ? 1 : 0;
}
