using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DevInterface;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static partial class WeatherSpatialSelectionUiCleanup
{
    // The weather picker and the Region FamWeather accordion may remain open independently.
    private static void CollapseFamilySchedulePopup()
    {
    }

    private sealed class FamilyScheduleTable : PositionedDevUINode
    {
        private const float HeaderY = 536f + FamilyTableExpansion;
        private const float FirstRowY = HeaderY - 22f;
        private const float RowStep = 17f;
        private const float GroupGap = 2f;

        private static readonly Dictionary<string, string> ExpandedFamilyByRegion =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly DevUINode _editor;
        private readonly FieldInfo _regionIdField;
        private readonly FieldInfo _statusField;
        private readonly MethodInfo _runValidationMethod;
        private readonly MethodInfo _updateStateLabelsMethod;

        internal FamilyScheduleTable(
            DevInterface.DevUI owner,
            DevUINode parent,
            DevUINode editor)
            : base(owner, "DryCycle_Weather_Family_Schedule_Table", parent, Vector2.zero)
        {
            _editor = editor;
            Type editorType = editor.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _regionIdField = editorType.GetField("_regionId", flags);
            _statusField = editorType.GetField("_status", flags);
            _runValidationMethod = editorType.GetMethod("RunValidation", flags);
            _updateStateLabelsMethod = editorType.GetMethod("UpdateStateLabels", flags);
            RebuildRows();
        }

        internal bool TryGetFamilyState(string familyId, out bool enabled, out float chancePercent)
        {
            return WeatherSpatialRegistry.TryGetFamilySchedule(
                RegionId,
                familyId,
                out enabled,
                out chancePercent);
        }

        internal bool TryGetSubWeatherState(
            in WeatherSpatialMember member,
            out bool enabled,
            out float chancePercent)
        {
            return WeatherSpatialRegistry.TryGetSubWeatherSchedule(
                RegionId,
                member.Kind,
                member.Id,
                out enabled,
                out chancePercent);
        }

        internal void SetFamilyEnabled(string familyId, bool enabled)
        {
            if (!WeatherSpatialRegistry.SetFamilyScheduleEnabled(RegionId, familyId, enabled))
            {
                SetStatus("Could not update " + familyId + " FamWeather state.");
                return;
            }

            SetStatus(familyId + " FamWeather: " + (enabled ? "YES" : "NO"));
            RefreshEditorAfterScheduleEdit();
        }

        internal void SetFamilyChance(string familyId, int percent)
        {
            if (!WeatherSpatialRegistry.SetFamilyScheduleChance(RegionId, familyId, percent))
            {
                SetStatus("Could not update " + familyId + " FamWeatherChance.");
                return;
            }

            SetStatus(familyId + " FamWeatherChance: " + percent + "%");
            RefreshEditorAfterScheduleEdit();
        }

        internal void SetSubWeatherEnabled(in WeatherSpatialMember member, bool enabled)
        {
            if (!WeatherSpatialRegistry.SetSubWeatherScheduleEnabled(
                    RegionId,
                    member.Kind,
                    member.Id,
                    enabled))
            {
                SetStatus("Could not update " + DisplayMember(member) + " state.");
                return;
            }

            SetStatus(DisplayMember(member) + ": " + (enabled ? "YES" : "NO"));
            RefreshEditorAfterScheduleEdit();
        }

        internal void SetSubWeatherChance(in WeatherSpatialMember member, int percent)
        {
            if (!WeatherSpatialRegistry.SetSubWeatherScheduleChance(
                    RegionId,
                    member.Kind,
                    member.Id,
                    percent))
            {
                SetStatus("Could not update " + DisplayMember(member) + " chance.");
                return;
            }

            SetStatus(DisplayMember(member) + " Chance: " + percent + "%");
            RefreshEditorAfterScheduleEdit();
        }

        internal bool IsExpanded(string familyId)
        {
            return ExpandedFamilyByRegion.TryGetValue(RegionId, out string expanded) &&
                   string.Equals(expanded, familyId, StringComparison.OrdinalIgnoreCase);
        }

        internal void ToggleExpanded(string familyId)
        {
            string regionId = RegionId;
            if (ExpandedFamilyByRegion.TryGetValue(regionId, out string expanded) &&
                string.Equals(expanded, familyId, StringComparison.OrdinalIgnoreCase))
            {
                ExpandedFamilyByRegion.Remove(regionId);
            }
            else
            {
                // Accordion behavior keeps the panel within the existing DevUI height:
                // one FamWeather can expose its SubWeather rows at a time.
                ExpandedFamilyByRegion[regionId] = familyId;
            }

            RebuildRows();
            _editor?.Refresh();
        }

        private void RebuildRows()
        {
            for (int i = subNodes.Count - 1; i >= 0; i--)
            {
                DevUINode node = subNodes[i];
                subNodes.RemoveAt(i);
                node?.ClearSprites();
            }

            AddLabel("DryCycle_Weather_Family_Header_Name", 8f, HeaderY, 84f, "FamWeather");
            AddLabel("DryCycle_Weather_Family_Header_Enabled", 96f, HeaderY, 78f, "Enabled");
            AddLabel("DryCycle_Weather_Family_Header_Chance", 178f, HeaderY, 114f, "FamWeatherChance");

            float y = FirstRowY;
            for (int familyIndex = 0; familyIndex < WeatherSpatialCatalog.AllFamilies.Count; familyIndex++)
            {
                WeatherSpatialFamily family = WeatherSpatialCatalog.AllFamilies[familyIndex];
                AddLabel(
                    "DryCycle_Weather_Family_Name_" + family.Id,
                    8f,
                    y,
                    84f,
                    family.Id);

                subNodes.Add(new FamilyEnableButton(
                    owner,
                    "DryCycle_Weather_Family_Yes_" + family.Id,
                    this,
                    new Vector2(96f, y),
                    38f,
                    "YES",
                    this,
                    family.Id,
                    enabledValue: true));
                subNodes.Add(new FamilyEnableButton(
                    owner,
                    "DryCycle_Weather_Family_No_" + family.Id,
                    this,
                    new Vector2(136f, y),
                    38f,
                    "NO",
                    this,
                    family.Id,
                    enabledValue: false));
                subNodes.Add(new FamilyChanceField(
                    owner,
                    "DryCycle_Weather_Family_Chance_" + family.Id,
                    this,
                    new Vector2(178f, y),
                    68f,
                    this,
                    family.Id));
                subNodes.Add(new FamilyExpandButton(
                    owner,
                    "DryCycle_Weather_Family_Expand_" + family.Id,
                    this,
                    new Vector2(250f, y),
                    42f,
                    this,
                    family.Id));

                y -= RowStep;
                if (IsExpanded(family.Id))
                {
                    for (int memberIndex = 0; memberIndex < family.Members.Count; memberIndex++)
                    {
                        WeatherSpatialMember member = family.Members[memberIndex];
                        AddLabel(
                            "DryCycle_Weather_SubWeather_Name_" + family.Id + "_" + memberIndex,
                            24f,
                            y,
                            140f,
                            DisplayMember(member));
                        subNodes.Add(new SubWeatherEnableButton(
                            owner,
                            "DryCycle_Weather_SubWeather_Yes_" + family.Id + "_" + memberIndex,
                            this,
                            new Vector2(168f, y),
                            36f,
                            "YES",
                            this,
                            member,
                            enabledValue: true));
                        subNodes.Add(new SubWeatherEnableButton(
                            owner,
                            "DryCycle_Weather_SubWeather_No_" + family.Id + "_" + memberIndex,
                            this,
                            new Vector2(206f, y),
                            36f,
                            "NO",
                            this,
                            member,
                            enabledValue: false));
                        subNodes.Add(new SubWeatherChanceField(
                            owner,
                            "DryCycle_Weather_SubWeather_Chance_" + family.Id + "_" + memberIndex,
                            this,
                            new Vector2(244f, y),
                            48f,
                            this,
                            member));
                        y -= RowStep;
                    }
                }

                if (familyIndex < WeatherSpatialCatalog.AllFamilies.Count - 1)
                {
                    y -= GroupGap;
                }
            }

            Refresh();
        }

        private void RefreshEditorAfterScheduleEdit()
        {
            _runValidationMethod?.Invoke(_editor, null);
            _updateStateLabelsMethod?.Invoke(_editor, null);
            _editor?.Refresh();
        }

        private void SetStatus(string text)
        {
            _statusField?.SetValue(_editor, text);
        }

        private string RegionId =>
            (_regionIdField?.GetValue(_editor) as string ?? string.Empty).Trim().ToUpperInvariant();

        private DevUILabel AddLabel(string id, float x, float y, float width, string text)
        {
            DevUILabel label = new(owner, id, this, new Vector2(x, y), width, text);
            label.spriteColor = Color.black;
            label.textColor = Color.white;
            subNodes.Add(label);
            return label;
        }

        private static string DisplayMember(in WeatherSpatialMember member)
        {
            return member.Kind == WeatherScheduleEventKind.DangerType
                ? "[Danger] " + member.Id
                : member.Id;
        }

        private static void PaintBinaryButton(Button button, bool selected)
        {
            if (selected)
            {
                button.spriteColor = new Color(0.62f, 0.08f, 0.08f);
                button.textColor = Color.white;
            }
            else if (!button.MouseOver)
            {
                button.spriteColor = new Color(0.82f, 0.82f, 0.82f);
                button.textColor = new Color(0.78f, 0.08f, 0.08f);
            }
        }

        private sealed class FamilyEnableButton : Button
        {
            private readonly FamilyScheduleTable _table;
            private readonly string _familyId;
            private readonly bool _enabledValue;

            internal FamilyEnableButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                string text,
                FamilyScheduleTable table,
                string familyId,
                bool enabledValue)
                : base(owner, id, parent, pos, width, text)
            {
                _table = table;
                _familyId = familyId;
                _enabledValue = enabledValue;
            }

            public override void Clicked()
            {
                _table?.SetFamilyEnabled(_familyId, _enabledValue);
            }

            public override void Update()
            {
                base.Update();
                bool enabled = false;
                bool configured = _table != null &&
                                  _table.TryGetFamilyState(_familyId, out enabled, out _);
                bool selected = _enabledValue
                    ? configured && enabled
                    : !configured || !enabled;
                PaintBinaryButton(this, selected);
            }
        }

        private sealed class SubWeatherEnableButton : Button
        {
            private readonly FamilyScheduleTable _table;
            private readonly WeatherSpatialMember _member;
            private readonly bool _enabledValue;

            internal SubWeatherEnableButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                string text,
                FamilyScheduleTable table,
                in WeatherSpatialMember member,
                bool enabledValue)
                : base(owner, id, parent, pos, width, text)
            {
                _table = table;
                _member = member;
                _enabledValue = enabledValue;
            }

            public override void Clicked()
            {
                _table?.SetSubWeatherEnabled(_member, _enabledValue);
            }

            public override void Update()
            {
                base.Update();
                bool enabled = false;
                bool configured = _table != null &&
                                  _table.TryGetSubWeatherState(_member, out enabled, out _);
                bool selected = _enabledValue
                    ? configured && enabled
                    : !configured || !enabled;
                PaintBinaryButton(this, selected);
            }
        }

        private sealed class FamilyExpandButton : Button
        {
            private readonly FamilyScheduleTable _table;
            private readonly string _familyId;

            internal FamilyExpandButton(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                FamilyScheduleTable table,
                string familyId)
                : base(owner, id, parent, pos, width, string.Empty)
            {
                _table = table;
                _familyId = familyId;
            }

            public override void Clicked()
            {
                _table?.ToggleExpanded(_familyId);
            }

            public override void Update()
            {
                base.Update();
                Text = _table != null && _table.IsExpanded(_familyId) ? "▼" : "▶";
            }
        }

        private abstract class PercentField : Button
        {
            private bool _editing;
            private string _buffer = string.Empty;

            protected PercentField(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width)
                : base(owner, id, parent, pos, width, "--")
            {
            }

            protected abstract bool TryRead(out float chancePercent);
            protected abstract void Write(int percent);

            public override void Clicked()
            {
                int value = TryRead(out float chance)
                    ? Mathf.RoundToInt(chance)
                    : 100;
                _editing = true;
                _buffer = value.ToString(CultureInfo.InvariantCulture);
                Text = _buffer + "_";
                if (owner != null)
                {
                    owner.mouseClick = false;
                }
            }

            public override void Update()
            {
                base.Update();
                if (!_editing)
                {
                    Text = TryRead(out float chance)
                        ? Mathf.RoundToInt(chance).ToString(CultureInfo.InvariantCulture) + "%"
                        : "--";
                    return;
                }

                bool commit = false;
                bool cancel = false;
                string typed = Input.inputString ?? string.Empty;
                for (int i = 0; i < typed.Length; i++)
                {
                    char c = typed[i];
                    if (c >= '0' && c <= '9')
                    {
                        if (_buffer.Length < 3)
                        {
                            _buffer += c;
                        }
                    }
                    else if (c == '\b')
                    {
                        if (_buffer.Length > 0)
                        {
                            _buffer = _buffer.Substring(0, _buffer.Length - 1);
                        }
                    }
                    else if (c == '\n' || c == '\r')
                    {
                        commit = true;
                    }
                }

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    cancel = true;
                }
                if (owner != null && owner.mouseClick && !MouseOver)
                {
                    commit = true;
                }

                if (cancel)
                {
                    _editing = false;
                    return;
                }
                if (commit)
                {
                    Commit();
                    return;
                }

                Text = _buffer.Length == 0 ? "_" : _buffer + "_";
            }

            private void Commit()
            {
                if (!int.TryParse(
                        _buffer,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int percent) ||
                    percent < 0 ||
                    percent > 100)
                {
                    Text = _buffer.Length == 0 ? "_" : _buffer + "_";
                    return;
                }

                Write(percent);
                _editing = false;
                _buffer = percent.ToString(CultureInfo.InvariantCulture);
                Text = _buffer + "%";
            }
        }

        private sealed class FamilyChanceField : PercentField
        {
            private readonly FamilyScheduleTable _table;
            private readonly string _familyId;

            internal FamilyChanceField(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                FamilyScheduleTable table,
                string familyId)
                : base(owner, id, parent, pos, width)
            {
                _table = table;
                _familyId = familyId;
            }

            protected override bool TryRead(out float chancePercent)
            {
                chancePercent = 0f;
                return _table != null &&
                       _table.TryGetFamilyState(_familyId, out _, out chancePercent);
            }

            protected override void Write(int percent)
            {
                _table?.SetFamilyChance(_familyId, percent);
            }
        }

        private sealed class SubWeatherChanceField : PercentField
        {
            private readonly FamilyScheduleTable _table;
            private readonly WeatherSpatialMember _member;

            internal SubWeatherChanceField(
                DevInterface.DevUI owner,
                string id,
                DevUINode parent,
                Vector2 pos,
                float width,
                FamilyScheduleTable table,
                in WeatherSpatialMember member)
                : base(owner, id, parent, pos, width)
            {
                _table = table;
                _member = member;
            }

            protected override bool TryRead(out float chancePercent)
            {
                chancePercent = 0f;
                return _table != null &&
                       _table.TryGetSubWeatherState(_member, out _, out chancePercent);
            }

            protected override void Write(int percent)
            {
                _table?.SetSubWeatherChance(_member, percent);
            }
        }
    }
}
