using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class AbstractRopeSpear : AbstractSpear
{
    public const float DefaultRopeLength = 260f;
    public const float MinRopeLength = 65f;

    // Rope is allowed to pay out freely during a throw. Keep the authored/runtime
    // ceiling well above any normal Rain World room span so projectile flight is
    // never range-limited by the old 360 px cap. Reeling can still shorten it all
    // the way back to MinRopeLength.
    public const float MaxRopeLength = 10000f;

    internal const string FixedHandlePrefix = "DRYCYCLE_ROPESPEAR_FIXED_HANDLE=";
    internal const string FixedHandleAnchorPrefix = "DRYCYCLE_ROPESPEAR_FIXED_ANCHOR=";
    internal const string StuckDirectionPrefix = "DRYCYCLE_ROPESPEAR_STUCK_DIR=";

    public float RopeLength;
    public bool RopeBroken;
    public bool HasPersistentHandleAnchor;
    public Vector2 PersistentHandleAnchor;

    private Vector2 _persistentStuckDirection;

    public AbstractRopeSpear(
        World world,
        WorldCoordinate pos,
        EntityID id,
        float ropeLength = DefaultRopeLength,
        bool ropeBroken = false)
        : base(world, null, pos, id, explosive: false)
    {
        type = RopeSpearHooks.ObjectType;
        RopeLength = Mathf.Clamp(ropeLength, MinRopeLength, MaxRopeLength);
        RopeBroken = ropeBroken;
        HasPersistentHandleAnchor = false;
        PersistentHandleAnchor = Vector2.zero;
        _persistentStuckDirection = Vector2.zero;
    }

    internal bool TryGetPersistentStuckDirection(out Vector2 direction)
    {
        if (_persistentStuckDirection.sqrMagnitude > 0.25f)
        {
            direction = _persistentStuckDirection.normalized;
            return true;
        }

        if (unrecognizedAttributes != null)
        {
            for (int i = 0; i < unrecognizedAttributes.Length; i++)
            {
                string attribute = unrecognizedAttributes[i];
                if (string.IsNullOrEmpty(attribute) ||
                    !attribute.StartsWith(StuckDirectionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string payload = attribute.Substring(StuckDirectionPrefix.Length);
                string[] pieces = payload.Split(',');
                if (pieces.Length != 2 ||
                    !float.TryParse(
                        pieces[0],
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out float x) ||
                    !float.TryParse(
                        pieces[1],
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out float y))
                {
                    continue;
                }

                Vector2 parsed = new(x, y);
                if (parsed.sqrMagnitude <= 0.25f)
                {
                    continue;
                }

                _persistentStuckDirection = parsed.normalized;
                direction = _persistentStuckDirection;
                return true;
            }
        }

        direction = Vector2.zero;
        return false;
    }

    internal void SetPersistentStuckDirection(Vector2 direction)
    {
        bool keepDirection = direction.sqrMagnitude > 0.25f;
        _persistentStuckDirection = keepDirection
            ? direction.normalized
            : Vector2.zero;

        List<string> attributes = new();
        if (unrecognizedAttributes != null)
        {
            for (int i = 0; i < unrecognizedAttributes.Length; i++)
            {
                string attribute = unrecognizedAttributes[i];
                if (!string.IsNullOrEmpty(attribute) &&
                    !attribute.StartsWith(StuckDirectionPrefix, StringComparison.Ordinal))
                {
                    attributes.Add(attribute);
                }
            }
        }

        if (keepDirection)
        {
            attributes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1:R},{2:R}",
                StuckDirectionPrefix,
                _persistentStuckDirection.x,
                _persistentStuckDirection.y));
        }

        unrecognizedAttributes = attributes.Count > 0
            ? attributes.ToArray()
            : null;
    }

    public override string ToString()
    {
        string baseString = base.ToString();
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>DRYCYCLE_ROPESPEAR_LENGTH={1}" +
            "<oA>DRYCYCLE_ROPESPEAR_BROKEN={2}" +
            "<oA>{3}{4}" +
            "<oA>{5}{6},{7}",
            baseString,
            RopeLength,
            RopeBroken ? 1 : 0,
            FixedHandlePrefix,
            HasPersistentHandleAnchor ? 1 : 0,
            FixedHandleAnchorPrefix,
            PersistentHandleAnchor.x,
            PersistentHandleAnchor.y);
    }
}
