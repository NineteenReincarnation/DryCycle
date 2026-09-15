using UnityEngine;

namespace DryCycle.Items.ScavengerLance;

internal sealed partial class ScavengerLance
{
    /// <summary>
    /// Combat-only angular rig. Normal carry follows the vanilla scavenger hand direction directly;
    /// only authored attack poses keep extra angular smoothing for brace/charge/counter-sweep.
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

            // Vanilla-style carry must stay free in the hand. Do not add the heavy-weapon spring,
            // acceleration lag or angular speed cap unless a deliberate combat pose owns the lance.
            bool authoredCombatPose = grip.Braced || grip.Charging || grip.CounterSweep || grip.AimTracking;
            if (!authoredCombatPose)
            {
                _initialized = true;
                _holder = holder;
                _angle = DirectionAngle(desired);
                _angularVelocity = 0f;
                _lastHolderVelocity = holder.mainBodyChunk.vel;
                return desired;
            }

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

            if (!grip.Charging && !grip.CounterSweep)
            {
                float facing = Mathf.Sign(desired.x);
                if (facing == 0f) facing = 1f;
                float influence = grip.AimTracking ? 0.18f : 0.42f;
                float limit = grip.AimTracking ? 1.5f : 3.5f;
                float inertialOffset = (-acceleration.x * 1.35f + acceleration.y * 0.45f * facing) * influence;
                desiredAngle += Mathf.Clamp(inertialOffset, -limit, limit);
            }

            float stiffness;
            float damping;
            float maximumAngularSpeed;
            if (grip.CounterSweep)
            {
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
            else
            {
                stiffness = 0.22f;
                damping = 0.62f;
                maximumAngularSpeed = 12f;
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
