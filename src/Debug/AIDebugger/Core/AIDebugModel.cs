using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace DryCycle.Debugging.AI;

internal enum AIDebugLanguage
{
    Chinese,
    English
}

internal enum AIDebugEntityState
{
    Realized,
    Abstract,
    Shortcut,
    Den,
    Deleted
}

internal enum AIDebugDecisionState
{
    Active,
    Ready,
    Blocked,
    Inactive,
    Pass,
    Warning
}

// Stable across realize/unrealize. Do not retain a Creature reference as identity.
internal readonly struct DebugEntityKey : IEquatable<DebugEntityKey>
{
    internal readonly int Spawner;
    internal readonly int Number;
    internal readonly string Template;

    internal DebugEntityKey(int spawner, int number, string template)
    {
        Spawner = spawner;
        Number = number;
        Template = template ?? "?";
    }

    internal static DebugEntityKey From(AbstractCreature creature)
    {
        if (creature == null) return default;
        string template = creature.creatureTemplate?.type?.value ?? "?";
        return new DebugEntityKey(creature.ID.spawner, creature.ID.number, template);
    }

    public bool Equals(DebugEntityKey other) =>
        Spawner == other.Spawner && Number == other.Number &&
        string.Equals(Template, other.Template, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is DebugEntityKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Spawner;
            hash = hash * 397 ^ Number;
            hash = hash * 397 ^ (Template?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public static bool operator ==(DebugEntityKey left, DebugEntityKey right) => left.Equals(right);
    public static bool operator !=(DebugEntityKey left, DebugEntityKey right) => !left.Equals(right);

    public override string ToString() => $"{Template} {Spawner}:{Number}";
}

internal readonly struct AIDebugValue
{
    internal readonly string LabelKey;
    internal readonly string RawName;
    internal readonly string Value;
    internal readonly int AgeTicks;
    internal readonly string Source;

    internal AIDebugValue(string labelKey, string rawName, string value, int ageTicks = 0, string source = null)
    {
        LabelKey = labelKey;
        RawName = rawName;
        Value = value ?? "—";
        AgeTicks = Mathf.Max(0, ageTicks);
        Source = source;
    }
}

internal sealed class AIDebugSection
{
    internal readonly string TitleKey;
    internal readonly List<AIDebugValue> Values = new(12);

    internal AIDebugSection(string titleKey) => TitleKey = titleKey;

    internal AIDebugSection Add(string labelKey, string rawName, object value, int ageTicks = 0, string source = null)
    {
        Values.Add(new AIDebugValue(labelKey, rawName, AIDebugFormat.Value(value), ageTicks, source));
        return this;
    }
}

internal sealed class AIDebugDecisionNode
{
    internal readonly string LabelKey;
    internal readonly AIDebugDecisionState State;
    internal readonly string Detail;
    internal readonly string RawName;
    internal readonly int Depth;

    internal AIDebugDecisionNode(string labelKey, AIDebugDecisionState state, string detail = null,
        string rawName = null, int depth = 0)
    {
        LabelKey = labelKey;
        State = state;
        Detail = detail;
        RawName = rawName;
        Depth = Mathf.Max(0, depth);
    }
}

internal sealed class AIDebugSnapshot
{
    internal readonly DebugEntityKey Key;
    internal readonly string DisplayName;
    internal readonly AIDebugEntityState EntityState;
    internal readonly string ControlOwner;
    internal readonly List<AIDebugSection> Sections = new(8);
    internal readonly List<AIDebugDecisionNode> Decisions = new(16);

    internal AIDebugSnapshot(DebugEntityKey key, string displayName, AIDebugEntityState entityState, string controlOwner)
    {
        Key = key;
        DisplayName = displayName ?? key.ToString();
        EntityState = entityState;
        ControlOwner = controlOwner ?? "—";
    }
}

internal interface IAIDebugSource
{
    int Priority { get; }
    bool CanInspect(AbstractCreature creature);
    AIDebugSnapshot Capture(AbstractCreature creature, RainWorldGame game);
}

internal static class AIDebugFormat
{
    internal static string Value(object value)
    {
        return value switch
        {
            null => "—",
            bool b => b ? "true" : "false",
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            Vector2 v => $"({v.x.ToString("0.0", CultureInfo.InvariantCulture)}, {v.y.ToString("0.0", CultureInfo.InvariantCulture)})",
            WorldCoordinate c => c.ToString(),
            EntityID id => id.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "—"
        };
    }

    internal static string Creature(Creature creature)
    {
        if (creature?.abstractCreature == null) return "—";
        string type = creature.abstractCreature.creatureTemplate?.type?.value ?? creature.GetType().Name;
        return $"{type} #{creature.abstractCreature.ID.number}";
    }
}
