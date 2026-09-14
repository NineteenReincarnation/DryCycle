using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.LanceScavenger;

/// <summary>Short damped cords, shared by two battle ribbons and the asymmetric pearl strings.</summary>
internal sealed class LanceAdornment
{
    internal readonly Vector2[] Positions;
    internal readonly Vector2[] LastPositions;
    private readonly Vector2[] _velocities;
    private readonly float _spacing;
    private bool _initialized;
    internal LanceAdornment(int nodes, float spacing)
    { Positions = new Vector2[nodes]; LastPositions = new Vector2[nodes]; _velocities = new Vector2[nodes]; _spacing = spacing; }
    internal void Reset(Vector2 root)
    {
        for (int i = 0; i < Positions.Length; i++)
        { Positions[i] = LastPositions[i] = root + Vector2.down * (i * _spacing); _velocities[i] = Vector2.zero; }
        _initialized = true;
    }
    internal void Update(Vector2 root, Vector2 ownerVelocity, float gravity)
    {
        if (!_initialized || Vector2.Distance(Positions[0], root) > 90f) Reset(root);
        for (int i = 0; i < Positions.Length; i++) LastPositions[i] = Positions[i];
        Positions[0] = root;
        for (int i = 1; i < Positions.Length; i++)
        {
            _velocities[i] = Vector2.ClampMagnitude(_velocities[i] * 0.78f + Vector2.down * gravity * 0.5f - ownerVelocity * 0.025f, 8f);
            Vector2 next = Positions[i] + _velocities[i];
            next = Positions[i - 1] + Vector2.ClampMagnitude(next - Positions[i - 1], _spacing);
            _velocities[i] = next - Positions[i];
            Positions[i] = next;
        }
    }
    internal Vector2 At(int node, float t) => Vector2.Lerp(LastPositions[node], Positions[node], t);
    internal void DrawRibbon(TriangleMesh mesh, float t, Vector2 camera, float width)
    {
        for (int i = 0; i < Positions.Length - 1; i++)
        {
            Vector2 a = At(i, t), b = At(i + 1, t);
            Vector2 perp = Custom.PerpendicularVector((b - a).normalized);
            float wa = width * (1f - i / (float)Positions.Length * 0.7f);
            float wb = width * (1f - (i + 1) / (float)Positions.Length * 0.7f);
            mesh.MoveVertice(i * 4, a - perp * wa - camera);
            mesh.MoveVertice(i * 4 + 1, a + perp * wa - camera);
            mesh.MoveVertice(i * 4 + 2, b - perp * wb - camera);
            mesh.MoveVertice(i * 4 + 3, b + perp * wb - camera);
        }
    }
}
