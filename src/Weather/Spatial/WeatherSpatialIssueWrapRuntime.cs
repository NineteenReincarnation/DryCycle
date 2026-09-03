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
    private const int MaxIssueSlots = 7;
    private const int MaxCharsPerLine = 58;
    private const float LineHeight = 16f;
    private const float IssueGap = 2f;
    private const float BottomMargin = 8f;

    private static readonly DevUILabel[] Labels = new DevUILabel[MaxIssueSlots];
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
        _enabled = false;
    }

    private static void MapPage_Update(
        On.DevInterface.MapPage.orig_Update orig,
        MapPage self)
    {
        orig(self);

        DevUINode editor = FindDirect(self, EditorNodeId);
        if (editor == null)
        {
            return;
        }

        RefreshLayout(editor);
    }

    private static void RefreshLayout(DevUINode editor)
    {
        DevUINode pathNode = FindDirect(editor, PathNodeId);
        if (pathNode is not PositionedDevUINode path)
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
        float cursorTop = path.pos.y - IssueGap;
        int issueIndex = 0;
        int slot = 0;

        while (slot < Labels.Length)
        {
            if (issueIndex >= issueCount)
            {
                HideFrom(slot);
                return;
            }

            int remainingIssues = issueCount - issueIndex;
            if (slot == Labels.Length - 1 && remainingIssues > 1)
            {
                ShowSummary(Labels[slot], cursorTop, remainingIssues);
                HideFrom(slot + 1);
                return;
            }

            string wrapped = WrapIssue(validation.Issues[issueIndex].ToString(), out int lineCount);
            float height = Mathf.Max(LineHeight, lineCount * LineHeight);

            if (cursorTop - height < BottomMargin)
            {
                ShowSummary(Labels[slot], cursorTop, remainingIssues);
                HideFrom(slot + 1);
                return;
            }

            ShowLabel(Labels[slot], wrapped, lineCount, cursorTop);
            cursorTop -= height + IssueGap;
            issueIndex++;
            slot++;
        }
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

    private static void ShowSummary(DevUILabel label, float top, int count)
    {
        if (label == null || top - LineHeight < BottomMargin)
        {
            if (label != null)
            {
                Hide(label);
            }
            return;
        }

        ShowLabel(label, "+ " + count + " more issue(s)", 1, top);
    }

    private static void ShowLabel(
        DevUILabel label,
        string text,
        int lineCount,
        float top)
    {
        float height = Mathf.Max(LineHeight, lineCount * LineHeight);
        label.Text = text ?? string.Empty;
        label.pos = new Vector2(label.pos.x, top);
        label.size = new Vector2(label.size.x, height);

        if (label.fSprites.Count > 0 && label.fSprites[0] != null)
        {
            label.fSprites[0].anchorY = 1f;
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
        const string continuationIndent = "      ";
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
