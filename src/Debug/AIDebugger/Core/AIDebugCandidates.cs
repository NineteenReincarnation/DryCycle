using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal readonly struct AIDebugCandidate
{
    internal readonly string Set;
    internal readonly string Name;
    internal readonly Vector2 Position;
    internal readonly bool HasPosition;
    internal readonly bool Valid;
    internal readonly float Score;
    internal readonly string Reason;
    internal readonly bool Winner;
    internal readonly int Frame;

    internal AIDebugCandidate(string set, string name, Vector2 position, bool hasPosition,
        bool valid, float score, string reason, bool winner)
    {
        Set = set ?? "Candidate";
        Name = name ?? "?";
        Position = position;
        HasPosition = hasPosition;
        Valid = valid;
        Score = float.IsNaN(score) || float.IsInfinity(score) ? 0f : score;
        Reason = reason ?? string.Empty;
        Winner = winner;
        Frame = AIDebugTrace.SimulationTick;
    }
}

internal static class AIDebugCandidateRegistry
{
    private sealed class CandidateSet
    {
        internal int Frame = int.MinValue;
        internal readonly List<AIDebugCandidate> Items = new(24);
    }

    private static readonly Dictionary<DebugEntityKey, CandidateSet> Sets = new();

    internal static void Begin(AbstractCreature owner)
    {
        if (!AIDebugTrace.IsWatched(owner)) return;
        DebugEntityKey key = DebugEntityKey.From(owner);
        if (!Sets.TryGetValue(key, out CandidateSet set))
        {
            set = new CandidateSet();
            Sets[key] = set;
        }
        int tick = AIDebugTrace.SimulationTick;
        if (set.Frame == tick) return;
        set.Frame = tick;
        set.Items.Clear();
    }

    internal static void Record(AbstractCreature owner, string set, string name,
        Vector2 position, bool valid, float score = 0f, string reason = null, bool winner = false)
    {
        if (!AIDebugTrace.IsWatched(owner)) return;
        Begin(owner);
        CandidateSet target = Sets[DebugEntityKey.From(owner)];
        if (target.Items.Count >= 64) return;
        target.Items.Add(new AIDebugCandidate(set, name, position, true, valid, score, reason, winner));
    }

    internal static void Record(AbstractCreature owner, string set, string name,
        bool valid, float score = 0f, string reason = null, bool winner = false)
    {
        if (!AIDebugTrace.IsWatched(owner)) return;
        Begin(owner);
        CandidateSet target = Sets[DebugEntityKey.From(owner)];
        if (target.Items.Count >= 64) return;
        target.Items.Add(new AIDebugCandidate(set, name, Vector2.zero, false, valid, score, reason, winner));
    }

    internal static int Copy(DebugEntityKey key, List<AIDebugCandidate> output)
    {
        output.Clear();
        if (!Sets.TryGetValue(key, out CandidateSet set)) return 0;
        output.AddRange(set.Items);
        return output.Count;
    }

    internal static void Clear(DebugEntityKey key) => Sets.Remove(key);
    internal static void Reset() => Sets.Clear();
}
