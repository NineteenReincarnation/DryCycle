using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Relationships;

public enum EditorRelationshipDirection
{
    PrimaryToOther,
    OtherToPrimary
}

public sealed class EditorRelationshipValueSnapshot
{
    public string Type { get; init; } = string.Empty;
    public float Intensity { get; init; }
    public bool DirectOverride { get; init; }
}

public sealed class EditorRelationshipRowSnapshot
{
    public string CreatureType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Selected { get; init; }
    public EditorRelationshipValueSnapshot PrimaryToOther { get; init; } = new();
    public EditorRelationshipValueSnapshot OtherToPrimary { get; init; } = new();
}

public sealed class EditorRelationshipPresentationSnapshot
{
    public static readonly EditorRelationshipPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string PrimaryCreature { get; init; } = string.Empty;
    public string SelectedOtherCreature { get; init; } = string.Empty;
    public EditorRelationshipDirection SelectedDirection { get; init; }
    public string[] CreatureTypes { get; init; } = Array.Empty<string>();
    public string[] RelationshipTypes { get; init; } = Array.Empty<string>();
    public EditorRelationshipRowSnapshot[] Rows { get; init; } = Array.Empty<EditorRelationshipRowSnapshot>();
}

internal sealed class RelationshipEditorState
{
    internal string PrimaryCreature = string.Empty;
    internal string SelectedOtherCreature = string.Empty;
    internal EditorRelationshipDirection SelectedDirection = EditorRelationshipDirection.PrimaryToOther;
}

internal static class RelationshipEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, RelationshipEditorState> states = new();

    internal static RelationshipEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new RelationshipEditorState());

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, RelationshipEditorState>();
}

public static class RelationshipEditorPresentationHub
{
    private static volatile EditorRelationshipPresentationSnapshot current = EditorRelationshipPresentationSnapshot.Empty;
    private static string[] relationshipTypes = Array.Empty<string>();
    private static int relationshipTypeCount = -1;

    public static EditorRelationshipPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Relationships ||
            session.Owner?.activePage is not RelationshipPage)
        {
            current = EditorRelationshipPresentationSnapshot.Empty;
            return;
        }

        List<CreatureTemplate> templates = CollectTemplates();
        if (templates.Count == 0)
        {
            current = new EditorRelationshipPresentationSnapshot { Available = true };
            return;
        }

        RelationshipEditorState state = RelationshipEditorStateHub.Get(session);
        CreatureTemplate primary = FindTemplate(templates, state.PrimaryCreature) ?? templates[0];
        state.PrimaryCreature = primary.type.value;

        if (!string.IsNullOrEmpty(state.SelectedOtherCreature) &&
            FindTemplate(templates, state.SelectedOtherCreature) == null)
            state.SelectedOtherCreature = string.Empty;

        string[] creatureTypes = new string[templates.Count];
        List<EditorRelationshipRowSnapshot> rows = new(Math.Max(0, templates.Count - 1));
        for (int i = 0; i < templates.Count; i++)
        {
            CreatureTemplate other = templates[i];
            creatureTypes[i] = other.type.value;
            if (other.type == primary.type) continue;

            rows.Add(new EditorRelationshipRowSnapshot
            {
                CreatureType = other.type.value,
                DisplayName = string.IsNullOrEmpty(other.name) ? other.type.value : other.name,
                Selected = string.Equals(state.SelectedOtherCreature, other.type.value, StringComparison.Ordinal),
                PrimaryToOther = Capture(primary, other),
                OtherToPrimary = Capture(other, primary)
            });
        }

        RebuildRelationshipTypesIfNeeded();

        current = new EditorRelationshipPresentationSnapshot
        {
            Available = true,
            PrimaryCreature = state.PrimaryCreature,
            SelectedOtherCreature = state.SelectedOtherCreature,
            SelectedDirection = state.SelectedDirection,
            CreatureTypes = creatureTypes,
            RelationshipTypes = relationshipTypes,
            Rows = rows.ToArray()
        };
    }

    internal static void Clear() => current = EditorRelationshipPresentationSnapshot.Empty;

    private static EditorRelationshipValueSnapshot Capture(CreatureTemplate from, CreatureTemplate to)
    {
        CreatureTemplate.Relationship effective = RelationshipPage.GetEffectiveRelationship(from, to);
        bool direct = RelationshipPage.changedRelationships.TryGetValue(from.type, out Dictionary<CreatureTemplate.Type, CreatureTemplate.Relationship> changed) &&
                      changed.ContainsKey(to.type);
        return new EditorRelationshipValueSnapshot
        {
            Type = effective.type?.value ?? string.Empty,
            Intensity = effective.intensity,
            DirectOverride = direct
        };
    }

    private static List<CreatureTemplate> CollectTemplates()
    {
        List<CreatureTemplate> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < ExtEnum<CreatureTemplate.Type>.values.Count; i++)
        {
            string entry = ExtEnum<CreatureTemplate.Type>.values.GetEntry(i);
            if (string.IsNullOrEmpty(entry) || !seen.Add(entry)) continue;
            try
            {
                CreatureTemplate.Type type = new(entry, false);
                CreatureTemplate template = StaticWorld.GetCreatureTemplate(type);
                if (template?.type == null) continue;
                result.Add(template);
            }
            catch
            {
                // An ExtEnum entry may be registered before its template exists. It is safer
                // to skip it until StaticWorld can resolve it than to manufacture editor data.
            }
        }

        result.Sort((a, b) => string.Compare(a?.type?.value, b?.type?.value, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static CreatureTemplate FindTemplate(List<CreatureTemplate> templates, string typeName)
    {
        if (templates == null || string.IsNullOrEmpty(typeName)) return null;
        for (int i = 0; i < templates.Count; i++)
        {
            CreatureTemplate template = templates[i];
            if (template?.type != null && string.Equals(template.type.value, typeName, StringComparison.Ordinal))
                return template;
        }
        return null;
    }

    private static void RebuildRelationshipTypesIfNeeded()
    {
        int count = ExtEnum<CreatureTemplate.Relationship.Type>.values.Count;
        if (count == relationshipTypeCount) return;
        relationshipTypes = new string[count];
        for (int i = 0; i < count; i++)
            relationshipTypes[i] = ExtEnum<CreatureTemplate.Relationship.Type>.values.GetEntry(i);
        relationshipTypeCount = count;
    }
}

public enum RelationshipEditorCommandKind
{
    SelectPrimary,
    SelectPair,
    SetRelationshipType,
    SetRelationshipIntensity,
    ResetRelationship
}

public readonly struct RelationshipEditorCommand
{
    public RelationshipEditorCommand(
        RelationshipEditorCommandKind kind,
        string primary = null,
        string other = null,
        string text = null,
        float value = 0f,
        EditorRelationshipDirection direction = EditorRelationshipDirection.PrimaryToOther)
    {
        Kind = kind;
        Primary = primary;
        Other = other;
        Text = text;
        Value = value;
        Direction = direction;
    }

    public RelationshipEditorCommandKind Kind { get; }
    public string Primary { get; }
    public string Other { get; }
    public string Text { get; }
    public float Value { get; }
    public EditorRelationshipDirection Direction { get; }
}

public static class RelationshipEditorCommandQueue
{
    private static readonly ConcurrentQueue<RelationshipEditorCommand> queue = new();

    public static void Enqueue(RelationshipEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out RelationshipEditorCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case RelationshipEditorCommandKind.SelectPrimary:
                        RelationshipEditorActions.SelectPrimary(session, command.Primary);
                        break;
                    case RelationshipEditorCommandKind.SelectPair:
                        RelationshipEditorActions.SelectPair(session, command.Other, command.Direction);
                        break;
                    case RelationshipEditorCommandKind.SetRelationshipType:
                        RelationshipEditorActions.SetType(session, command.Primary, command.Other, command.Direction, command.Text);
                        break;
                    case RelationshipEditorCommandKind.SetRelationshipIntensity:
                        RelationshipEditorActions.SetIntensity(session, command.Primary, command.Other, command.Direction, command.Value);
                        break;
                    case RelationshipEditorCommandKind.ResetRelationship:
                        RelationshipEditorActions.Reset(session, command.Primary, command.Other, command.Direction);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool relationship command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
