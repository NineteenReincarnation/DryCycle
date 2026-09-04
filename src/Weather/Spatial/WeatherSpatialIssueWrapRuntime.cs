using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DevInterface;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static class WeatherSpatialIssueWrapRuntime
{
    private const string EditorNodeId = "DryCycle_WeatherSpatial";
    private const string PathNodeId = "Path";
    private const string ShortcutHeaderId = "WeatherShortcutHeader";
    private const int MaxIssueSlots = 7;
    private const int MaxCharsPerLine = 36;
    private const float IssueWidth = 270f;
    private const float LineHeight = 16f;
    private const float IssueGap = 3f;
    private const float TopGap = 18f;
    private const float ShortcutClearance = 28f;

    private sealed class WrappedIssue
    {
        internal string Text;
        internal int Lines;
        internal float Height;
    }

    private static readonly DevUILabel[] Labels = new DevUILabel[MaxIssueSlots];
    private static readonly List<WrappedIssue> VisibleIssues = new();
    private static bool _enabled;
    private static FieldInfo _lastValidationField;
    private static Type _cachedEditorType;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update += MapPage_Update;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.MapPage.Update -= MapPage_Update;
        _cachedEditorType = null;
        _lastValidationField = null;
        VisibleIssues.Clear();
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        orig(self);

        DevUINode editor = FindDirect(self, EditorNodeId);
        if (editor != null)
        {
            RefreshLayout(editor);
        }
    }

    private static void RefreshLayout(DevUINode editor)
    {
        if (FindDirect(editor, PathNodeId) is not PositionedDevUINode path)
        {
            return;
        }

        for (int i = 0; i < Labels.Length; i++)
        {
            Labels[i] = FindDirect(editor, "Issue" + i) as DevUILabel;
            if (Labels[i] == null)
            {
                return;
            }
        }

        WeatherSpatialValidationResult validation = GetValidation(editor);
        int issueCount = validation?.Issues?.Count ?? 0;
        if (issueCount <= 0)
        {
            HideFrom(0);
            return;
        }

        float top = path.pos.y - TopGap;
        float bottom = 120f;
        if (FindDirect(editor, ShortcutHeaderId) is PositionedDevUINode shortcut)
        {
            bottom = shortcut.pos.y + ShortcutClearance;
        }

        float available = Mathf.Max(0f, top - bottom);
        VisibleIssues.Clear();
        float used = 0f;

        // This is deliberately a bounded live diagnostics window. Keep newest issues
        // and let older overflow entries disappear instead of growing into Shortcuts.
        for (int issueIndex = issueCount - 1;
             issueIndex >= 0 && VisibleIssues.Count < MaxIssueSlots;
             issueIndex--)
        {
            string wrapped = WrapIssue(validation.Issues[issueIndex].ToString(), out int lineCount);
            float height = Mathf.Max(LineHeight, lineCount * LineHeight);
            float required = height + (VisibleIssues.Count > 0 ? IssueGap : 0f);
            if (used + required > available)
            {
                break;
            }

            VisibleIssues.Add(new WrappedIssue
            {
                Text = wrapped,
                Lines = lineCount,
                Height = height
            });
            used += required;
        }

        VisibleIssues.Reverse();
        float cursorTop = top;
        int slot = 0;
        for (; slot < VisibleIssues.Count && slot < Labels.Length; slot++)
        {
            WrappedIssue issue = VisibleIssues[slot];
            ShowLabel(Labels[slot], issue.Text, issue.Lines, cursorTop);
            cursorTop -= issue.Height + IssueGap;
        }

        HideFrom(slot);
    }

    private static WeatherSpatialValidationResult GetValidation(DevUINode editor)
    {
        Type editorType = editor.GetType();
        if (_cachedEditorType != editorType)
        {
            _cachedEditorType = editorType;
            _lastValidationField = editorType.GetField(
                "_lastValidation",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        return _lastValidationField?.GetValue(editor) as WeatherSpatialValidationResult;
    }

    private static void ShowLabel(
        DevUILabel label,
        string text,
        int lineCount,
        float top)
    {
        float height = Mathf.Max(LineHeight, lineCount * LineHeight);
        label.Text = text ?? string.Empty;
        label.pos = new Vector2(8f, top);
        label.size = new Vector2(IssueWidth, height);

        if (label.fSprites.Count > 0 && label.fSprites[0] != null)
        {
            label.fSprites[0].anchorY = 1f;
            label.fSprites[0].scaleX = IssueWidth;
            label.fSprites[0].scaleY = height;
            label.fSprites[0].isVisible = true;
        }

        if (label.fLabels.Count > 0 && label.fLabels[0] != null)
        {
            label.fLabels[0].anchorY = 1f;
            label.fLabels[0].isVisible = true;
        }

        label.Refresh();
    }

    private static void HideFrom(int start)
    {
        for (int i = Mathf.Max(0, start); i < Labels.Length; i++)
        {
            if (Labels[i] != null)
            {
                Hide(Labels[i]);
            }
        }
    }

    private static void Hide(DevUILabel label)
    {
        label.Text = string.Empty;
        if (label.fSprites.Count > 0 && label.fSprites[0] != null)
        {
            label.fSprites[0].isVisible = false;
        }
        if (label.fLabels.Count > 0 && label.fLabels[0] != null)
        {
            label.fLabels[0].isVisible = false;
        }
    }

    private static string WrapIssue(string text, out int lineCount)
    {
        const string continuationIndent = "    ";
        List<string> lines = new();
        string[] words = (text ?? string.Empty).Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);

        StringBuilder current = new();
        bool firstLine = true;
        int contentLimit = MaxCharsPerLine;

        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            while (word.Length > 0)
            {
                int available = contentLimit - current.Length - (current.Length > 0 ? 1 : 0);
                if (available <= 0)
                {
                    CommitLine(lines, current, firstLine, continuationIndent);
                    firstLine = false;
                    contentLimit = MaxCharsPerLine - continuationIndent.Length;
                    continue;
                }

                if (word.Length <= available)
                {
                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }
                    current.Append(word);
                    word = string.Empty;
                    continue;
                }

                if (current.Length > 0)
                {
                    CommitLine(lines, current, firstLine, continuationIndent);
                    firstLine = false;
                    contentLimit = MaxCharsPerLine - continuationIndent.Length;
                    continue;
                }

                int take = Mathf.Max(1, Mathf.Min(contentLimit, word.Length));
                current.Append(word.Substring(0, take));
                word = word.Substring(take);
                CommitLine(lines, current, firstLine, continuationIndent);
                firstLine = false;
                contentLimit = MaxCharsPerLine - continuationIndent.Length;
            }
        }

        if (current.Length > 0 || lines.Count == 0)
        {
            CommitLine(lines, current, firstLine, continuationIndent);
        }

        lineCount = Mathf.Max(1, lines.Count);
        return string.Join("\n", lines);
    }

    private static void CommitLine(
        List<string> lines,
        StringBuilder current,
        bool firstLine,
        string continuationIndent)
    {
        string content = current.ToString();
        lines.Add(firstLine ? content : continuationIndent + content);
        current.Length = 0;
    }

    private static DevUINode FindDirect(DevUINode parent, string id)
    {
        if (parent?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < parent.subNodes.Count; i++)
        {
            DevUINode node = parent.subNodes[i];
            if (node != null && node.IDstring == id)
            {
                return node;
            }
        }

        return null;
    }
}
