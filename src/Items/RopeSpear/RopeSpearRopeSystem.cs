using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Items.RopeSpear;

internal sealed class RopeSpearRopeSystem
{
    internal const int NodeCount = 30;

    private const int ConstraintIterations = 7;
    private const float VerletDamping = 0.985f;
    private const float NodeGravity = 0.42f;

    private struct Node
    {
        internal Vector2 Pos;
        internal Vector2 LastPos;
    }

    private readonly Node[] _nodes = new Node[NodeCount];
    private readonly bool[] _pinned = new bool[NodeCount];
    private readonly Vector2[] _pinTargets = new Vector2[NodeCount];
    private readonly List<Vector2> _guide = new();

    private Rope _topology;
    private Room _room;
    private bool _initialized;

    internal float RouteLength => _topology?.totalLength ?? 0f;

    internal bool Ready => _initialized && _topology != null;

    internal void Reset()
    {
        _topology?.Reset();
        _topology = null;
        _room = null;
        _initialized = false;
        _guide.Clear();
    }

    internal void Update(
        Room room,
        Vector2 endpointA,
        Vector2 endpointB,
        float ropeLength,
        float thickness)
    {
        if (room == null)
        {
            Reset();
            return;
        }

        if (_topology == null || _room != room)
        {
            _topology = new Rope(room, endpointA, endpointB, thickness);
            _room = room;
            _initialized = false;
        }

        _topology.Update(endpointA, endpointB);
        BuildGuide(endpointA, endpointB);

        if (!_initialized ||
            Vector2.Distance(_nodes[0].Pos, endpointA) > 140f ||
            Vector2.Distance(_nodes[NodeCount - 1].Pos, endpointB) > 140f)
        {
            InitializeNodes(ropeLength);
        }

        BuildPins(endpointA, endpointB);

        for (int i = 1; i < NodeCount - 1; i++)
        {
            if (_pinned[i])
            {
                continue;
            }

            Vector2 current = _nodes[i].Pos;
            Vector2 velocity = (current - _nodes[i].LastPos) * VerletDamping;
            _nodes[i].LastPos = current;
            _nodes[i].Pos += velocity;
            _nodes[i].Pos.y -= room.gravity * NodeGravity;
        }

        float segmentLength = Mathf.Max(2f, ropeLength / (NodeCount - 1f));
        for (int iteration = 0; iteration < ConstraintIterations; iteration++)
        {
            ApplyPins();

            for (int i = 0; i < NodeCount - 1; i++)
            {
                SolvePair(i, i + 1, segmentLength);
            }

            ApplyPins();
        }

        ResolveTerrain();
        ApplyPins();
    }

    internal Vector2 GetPoint(float normalizedPosition)
    {
        if (!_initialized)
        {
            return Vector2.zero;
        }

        float scaled = Mathf.Clamp01(normalizedPosition) * (NodeCount - 1f);
        int index = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, NodeCount - 2);
        float local = scaled - index;
        return Vector2.Lerp(_nodes[index].Pos, _nodes[index + 1].Pos, local);
    }

    internal bool TryFindNearestPoint(
        Vector2 worldPosition,
        float maxDistance,
        out float normalizedPosition,
        out float distance)
    {
        normalizedPosition = 0f;
        distance = float.MaxValue;
        if (!_initialized)
        {
            return false;
        }

        for (int i = 0; i < NodeCount - 1; i++)
        {
            Vector2 a = _nodes[i].Pos;
            Vector2 b = _nodes[i + 1].Pos;
            Vector2 ab = b - a;
            float denominator = ab.sqrMagnitude;
            float local = denominator <= 0.0001f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(worldPosition - a, ab) / denominator);
            Vector2 nearest = Vector2.Lerp(a, b, local);
            float currentDistance = Vector2.Distance(worldPosition, nearest);
            if (currentDistance >= distance)
            {
                continue;
            }

            distance = currentDistance;
            normalizedPosition = (i + local) / (NodeCount - 1f);
        }

        return distance <= maxDistance;
    }

    internal void ApplyExternalPull(float normalizedPosition, Vector2 target, float amount)
    {
        if (!_initialized)
        {
            return;
        }

        int center = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Clamp01(normalizedPosition) * (NodeCount - 1f)),
            1,
            NodeCount - 2);

        for (int offset = -2; offset <= 2; offset++)
        {
            int index = center + offset;
            if (index <= 0 || index >= NodeCount - 1 || _pinned[index])
            {
                continue;
            }

            float weight = 1f - Mathf.Abs(offset) / 3f;
            _nodes[index].Pos = Vector2.Lerp(
                _nodes[index].Pos,
                target,
                Mathf.Clamp01(amount * weight));
        }
    }

    internal void CopyPositions(Vector2[] output)
    {
        if (output == null || output.Length < NodeCount)
        {
            return;
        }

        for (int i = 0; i < NodeCount; i++)
        {
            output[i] = _nodes[i].Pos;
        }
    }

    private void BuildGuide(Vector2 endpointA, Vector2 endpointB)
    {
        _guide.Clear();
        _guide.Add(endpointA);

        if (_topology?.bends != null)
        {
            for (int i = 0; i < _topology.bends.Count; i++)
            {
                _guide.Add(_topology.bends[i].pos);
            }
        }

        _guide.Add(endpointB);
    }

    private void InitializeNodes(float ropeLength)
    {
        float routeLength = GuideLength();
        float slack = Mathf.Clamp(ropeLength - routeLength, 0f, 100f);

        for (int i = 0; i < NodeCount; i++)
        {
            float t = i / (NodeCount - 1f);
            Vector2 position = SampleGuide(t);
            if (_guide.Count == 2 && slack > 0f)
            {
                position.y -= Mathf.Sin(t * Mathf.PI) * slack * 0.28f;
            }

            _nodes[i].Pos = position;
            _nodes[i].LastPos = position;
        }

        _initialized = true;
    }

    private void BuildPins(Vector2 endpointA, Vector2 endpointB)
    {
        for (int i = 0; i < NodeCount; i++)
        {
            _pinned[i] = false;
        }

        Pin(0, endpointA);
        Pin(NodeCount - 1, endpointB);

        if (_guide.Count <= 2)
        {
            return;
        }

        float total = GuideLength();
        if (total <= 0.001f)
        {
            return;
        }

        float walked = 0f;
        for (int guideIndex = 1; guideIndex < _guide.Count - 1; guideIndex++)
        {
            walked += Vector2.Distance(_guide[guideIndex - 1], _guide[guideIndex]);
            int nodeIndex = Mathf.Clamp(
                Mathf.RoundToInt(walked / total * (NodeCount - 1f)),
                1,
                NodeCount - 2);
            Pin(nodeIndex, _guide[guideIndex]);
        }
    }

    private void Pin(int index, Vector2 position)
    {
        _pinned[index] = true;
        _pinTargets[index] = position;
    }

    private void ApplyPins()
    {
        for (int i = 0; i < NodeCount; i++)
        {
            if (!_pinned[i])
            {
                continue;
            }

            _nodes[i].Pos = _pinTargets[i];
            _nodes[i].LastPos = _pinTargets[i];
        }
    }

    private void SolvePair(int aIndex, int bIndex, float desiredLength)
    {
        Vector2 delta = _nodes[bIndex].Pos - _nodes[aIndex].Pos;
        float distance = delta.magnitude;
        if (distance <= 0.0001f)
        {
            return;
        }

        Vector2 correction = delta * ((distance - desiredLength) / distance);
        bool pinA = _pinned[aIndex];
        bool pinB = _pinned[bIndex];

        if (pinA && pinB)
        {
            return;
        }

        if (pinA)
        {
            _nodes[bIndex].Pos -= correction;
        }
        else if (pinB)
        {
            _nodes[aIndex].Pos += correction;
        }
        else
        {
            _nodes[aIndex].Pos += correction * 0.5f;
            _nodes[bIndex].Pos -= correction * 0.5f;
        }
    }

    private void ResolveTerrain()
    {
        if (_room == null)
        {
            return;
        }

        for (int i = 1; i < NodeCount - 1; i++)
        {
            if (_pinned[i] || !_room.GetTile(_nodes[i].Pos).Solid)
            {
                continue;
            }

            Vector2 fallback = _nodes[i].LastPos;
            if (_room.GetTile(fallback).Solid)
            {
                fallback = SampleGuide(i / (NodeCount - 1f));
            }

            if (!_room.GetTile(fallback).Solid)
            {
                _nodes[i].Pos = fallback;
                _nodes[i].LastPos = fallback;
            }
        }
    }

    private float GuideLength()
    {
        float total = 0f;
        for (int i = 1; i < _guide.Count; i++)
        {
            total += Vector2.Distance(_guide[i - 1], _guide[i]);
        }
        return total;
    }

    private Vector2 SampleGuide(float normalizedPosition)
    {
        if (_guide.Count == 0)
        {
            return Vector2.zero;
        }

        if (_guide.Count == 1)
        {
            return _guide[0];
        }

        float total = GuideLength();
        if (total <= 0.001f)
        {
            return _guide[0];
        }

        float target = Mathf.Clamp01(normalizedPosition) * total;
        float walked = 0f;
        for (int i = 1; i < _guide.Count; i++)
        {
            float length = Vector2.Distance(_guide[i - 1], _guide[i]);
            if (walked + length >= target || i == _guide.Count - 1)
            {
                float local = length <= 0.001f
                    ? 0f
                    : Mathf.Clamp01((target - walked) / length);
                return Vector2.Lerp(_guide[i - 1], _guide[i], local);
            }
            walked += length;
        }

        return _guide[_guide.Count - 1];
    }
}
