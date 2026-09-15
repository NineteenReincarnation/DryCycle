using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    /// <summary>
    /// Enhanced hand-held spear pose. The grip position still follows the wielder directly,
    /// while the long weapon keeps its own angular velocity so turning, bracing and sweeping
    /// read as a heavy hand-held object instead of a sprite rigidly welded to the body.
    /// </summary>
    private sealed class CarryRig
    {
        private bool _initialized;
        private Creature _holder;
        private float _angle;
        private float _angularVelocity;
        private Vector2 _lastHolderVelocity;

        internal void Reset()
        {
            _initialized = false;
            _holder = null;
            _angle = 0f;
            _angularVelocity = 0f;
            _lastHolderVelocity = Vector2.zero;
        }

        internal Vector2 Step(Creature holder, LanceGrip grip, Vector2 currentRotation)
        {
            Vector2 desired = grip.Direction.sqrMagnitude > 0.001f
                ? grip.Direction.normalized
                : currentRotation.sqrMagnitude > 0.001f ? currentRotation.normalized : Vector2.right;

            if (!_initialized || _holder != holder)
            {
                _initialized = true;
                _holder = holder;
                _angle = DirectionAngle(currentRotation.sqrMagnitude > 0.001f ? currentRotation : desired);
                _angularVelocity = 0f;
                _lastHolderVelocity = holder.mainBodyChunk.vel;
                return AngleDirection(_angle);
            }

            Vector2 holderVelocity = holder.mainBodyChunk.vel;
            Vector2 acceleration = holderVelocity - _lastHolderVelocity;
            _lastHolderVelocity = holderVelocity;

            float desiredAngle = DirectionAngle(desired);

            // Ordinary carry keeps a little movement lag. The effect is intentionally reduced while
            // presenting the lance forward and almost removed during the half-second aiming brace.
            // Charge and counter-sweep directions are combat-authored and receive no artificial sway.
            if (!grip.Charging && !grip.CounterSweep)
            {
                float facing = Mathf.Sign(desired.x);
                if (facing == 0f) facing = 1f;
                float influence = grip.AimTracking ? 0.18f : grip.Braced ? 0.42f : 1f;
                float limit = grip.AimTracking ? 1.5f : grip.Braced ? 3.5f : 8f;
                float inertialOffset = (-acceleration.x * 1.35f + acceleration.y * 0.45f * facing) * influence;
                desiredAngle += Mathf.Clamp(inertialOffset, -limit, limit);
            }

            float stiffness;
            float damping;
            float maximumAngularSpeed;
            if (grip.CounterSweep)
            {
                // Counter-sweep must still cover its authored 80/120 degree arc in eight ticks.
                stiffness = 0.75f;
                damping = 0.45f;
                maximumAngularSpeed = 30f;
            }
            else if (grip.Charging)
            {
                stiffness = 0.38f;
                damping = 0.55f;
                maximumAngularSpeed = 18f;
            }
            else if (grip.AimTracking)
            {
                stiffness = 0.30f;
                damping = 0.55f;
                maximumAngularSpeed = 14f;
            }
            else if (grip.Braced)
            {
                stiffness = 0.22f;
                damping = 0.62f;
                maximumAngularSpeed = 12f;
            }
            else
            {
                stiffness = 0.14f;
                damping = 0.72f;
                maximumAngularSpeed = 9f;
            }

            float error = Mathf.DeltaAngle(_angle, desiredAngle);
            _angularVelocity += error * stiffness;
            _angularVelocity *= damping;
            _angularVelocity = Mathf.Clamp(_angularVelocity, -maximumAngularSpeed, maximumAngularSpeed);

            if (Mathf.Abs(error) < 0.12f && Mathf.Abs(_angularVelocity) < 0.04f)
                _angularVelocity = 0f;

            _angle += _angularVelocity;
            return AngleDirection(_angle);
        }

        private static float DirectionAngle(Vector2 direction) =>
            Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

        private static Vector2 AngleDirection(float angle)
        {
            float radians = angle * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        }
    }

    private readonly CarryRig _carryRig = new();

    private Vector2 UpdateCarryRig(Creature holder, LanceGrip grip, Vector2 currentRotation) =>
        _carryRig.Step(holder, grip, currentRotation);

    private void ResetCarryRig() => _carryRig.Reset();
}
