using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather.Spatial;

internal static class WeatherSpatialDevUI
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }
        On.DevInterface.DevUI.SwitchPage += DevUI_SwitchPage;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }
        On.DevInterface.DevUI.SwitchPage -= DevUI_SwitchPage;
        WeatherSpatialPreview.Clear();
        _enabled = false;
    }

    private static void DevUI_SwitchPage(
        On.DevInterface.DevUI.orig_SwitchPage orig,
        DevInterface.DevUI self,
        int newPage)
    {
        if (newPage != 3)
        {
            WeatherSpatialPreview.Clear();
        }

        orig(self, newPage);

        if (self?.activePage is not MapPage mapPage)
        {
            return;
        }

        for (int i = 0; i < mapPage.subNodes.Count; i++)
        {
            if (mapPage.subNodes[i] is WeatherSpatialEditorNode)
            {
                return;
            }
        }

        WeatherSpatialEditorNode editor = new(self, mapPage);
        mapPage.subNodes.Add(editor);
        editor.Refresh();
    }

    private sealed class WeatherSpatialEditorNode : Panel, IDevUISignals
    {
        private sealed class RuleChange
        {
            internal readonly string RegionId;
            internal readonly string RoomName;
            internal readonly WeatherSpatialTarget Target;
            internal readonly WeatherSpatialRule Before;
            internal WeatherSpatialRule After;
            internal readonly bool IsDefault;

            internal RuleChange(
                string regionId,
                string roomName,
                in WeatherSpatialTarget target,
                WeatherSpatialRule before,
                WeatherSpatialRule after,
                bool isDefault)
            {
                RegionId = regionId;
                RoomName = roomName;
                Target = target;
                Before = before;
                After = after;
                IsDefault = isDefault;
            }
        }

        private sealed class EditCommand
        {
            internal readonly List<RuleChange> Changes = new();
            internal string Label;
        }

        private readonly MapPage _mapPage;
        private readonly World _world;
        private readonly string _regionId;
        private readonly List<RoomPanel> _roomPanels = new();
        private readonly Dictionary<RoomPanel, WeatherRoomOverlay> _overlays = new();
        private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<EditCommand> _undo = new();
        private readonly List<EditCommand> _redo = new();
        private readonly MapRoomMarquee _selectionMarquee;
        private readonly WeatherRoomHoverInfoPanel _hoverInfoPanel;

        private readonly Button _targetButton;
        private readonly Button _overviewButton;
        private readonly Button _defaultButton;
        private readonly Button _brushButton;
        private readonly Button _previewButton;
        private readonly Button _stopGateButton;
        private readonly Button _stopSubregionButton;
        private readonly DevUILabel _previewLabel;
        private readonly DevUILabel _selectionLabel;
        private readonly DevUILabel _statusLabel;
        private readonly DevUILabel _pathLabel;
        private readonly DevUILabel[] _issueLabels = new DevUILabel[7];

        private int _targetIndex;
        private WeatherSpatialRule _brush = WeatherSpatialRule.Allow;
        private bool _overview;
        private bool _stopAtGates = true;
        private bool _stopAtSubregion = true;
        private bool _previewActive;
        private float _previewIntensity = 0.75f;
        private bool _painting;
        private EditCommand _paintCommand;
        private readonly HashSet<string> _paintTouched = new(StringComparer.OrdinalIgnoreCase);
        private WeatherSpatialValidationResult _lastValidation;
        private string _status = "Ready";

        private WeatherSpatialTarget CurrentTarget =>
            WeatherSpatialCatalog.AllTargets[Mathf.Clamp(_targetIndex, 0, WeatherSpatialCatalog.AllTargets.Count - 1)];

        internal WeatherSpatialEditorNode(DevInterface.DevUI owner, MapPage mapPage)
            : base(
                owner,
                "DryCycle_WeatherSpatial",
                mapPage,
                new Vector2(1035f, 158f),
                new Vector2(310f, 590f),
                "DryCycle Weather Zones")
        {
            _mapPage = mapPage;
            _world = mapPage.world;
            _regionId = (_world?.region?.name ?? _world?.name ?? string.Empty).Trim().ToUpperInvariant();

            for (int i = 0; i < mapPage.subNodes.Count; i++)
            {
                if (mapPage.subNodes[i] is RoomPanel roomPanel)
                {
                    _roomPanels.Add(roomPanel);
                }
            }

            float y = 558f;
            AddLabel("Region", 8f, y, 294f, "Region: " + _regionId);
            y -= 22f;
            AddButton("TargetPrev", 8f, y, 28f, "<");
            _targetButton = AddButton("Target", 40f, y, 220f, "");
            AddButton("TargetNext", 264f, y, 28f, ">");
            y -= 22f;
            _overviewButton = AddButton("Overview", 8f, y, 90f, "Overview: OFF");
            _defaultButton = AddButton("Default", 102f, y, 190f, "");
            y -= 22f;
            _brushButton = AddButton("Brush", 8f, y, 140f, "");
            AddButton("ApplySelected", 152f, y, 140f, "Apply Brush to Sel");
            y -= 22f;
            AddButton("PreviewMinus", 8f, y, 28f, "-");
            _previewButton = AddButton("Preview", 40f, y, 118f, "Preview: OFF");
            AddButton("PreviewPlus", 162f, y, 28f, "+");
            _previewLabel = AddLabel("PreviewValue", 194f, y, 98f, "75%");
            y -= 25f;

            _selectionLabel = AddLabel("Selection", 8f, y, 284f, "Selected: 0");
            y -= 20f;
            AddButton("SelectAll", 8f, y, 90f, "Select All");
            AddButton("SelectNone", 102f, y, 90f, "None");
            AddButton("SelectInvert", 196f, y, 96f, "Invert");
            y -= 22f;
            AddButton("SelectSubregion", 8f, y, 140f, "Current Subregion");
            AddButton("SelectConnected", 152f, y, 140f, "Connected");
            y -= 22f;
            AddButton("SelectShelters", 8f, y, 90f, "Shelters");
            AddButton("SelectGates", 102f, y, 90f, "Gates");
            AddButton("SelectOffscreen", 196f, y, 96f, "Offscreen");
            y -= 22f;
            _stopGateButton = AddButton("StopGate", 8f, y, 140f, "Stop Gates: ON");
            _stopSubregionButton = AddButton("StopSubregion", 152f, y, 140f, "Stop Subregion: ON");
            y -= 25f;

            AddButton("ForceAllow", 8f, y, 90f, "Sel Allow");
            AddButton("ForceDeny", 102f, y, 90f, "Sel Deny");
            AddButton("ForceInherit", 196f, y, 96f, "Sel Inherit");
            y -= 22f;
            AddButton("Undo", 8f, y, 90f, "Undo");
            AddButton("Redo", 102f, y, 90f, "Redo");
            AddButton("Validate", 196f, y, 96f, "Validate");
            y -= 22f;
            AddButton("Save", 8f, y, 140f, "Save WeatherSpatial");
            AddButton("Repair", 152f, y, 140f, "Repair JSON");
            y -= 24f;

            _statusLabel = AddLabel("Status", 8f, y, 284f, "");
            y -= 18f;
            _pathLabel = AddLabel("Path", 8f, y, 284f, "");
            y -= 18f;
            for (int i = 0; i < _issueLabels.Length; i++)
            {
                _issueLabels[i] = AddLabel("Issue" + i, 8f, y, 284f, "");
                _issueLabels[i].spriteColor = new Color(0f, 0f, 0f);
                _issueLabels[i].textColor = new Color(1f, 1f, 1f);
                y -= 18f;
            }

            for (int i = 0; i < _roomPanels.Count; i++)
            {
                WeatherRoomOverlay overlay = new(owner, this, _roomPanels[i]);
                _overlays[_roomPanels[i]] = overlay;
                subNodes.Add(overlay);
            }

            _selectionMarquee = new MapRoomMarquee(
                owner,
                "DryCycle_Weather_Room_Marquee",
                this);
            subNodes.Add(_selectionMarquee);

            _hoverInfoPanel = new WeatherRoomHoverInfoPanel(owner, this);
            subNodes.Add(_hoverInfoPanel);

            collapsed = true;
            _lastValidation = WeatherSpatialRegistry.Validate(_world);
            UpdateStateLabels();
        }

        public override void Update()
        {
            base.Update();

            bool editorActive = !collapsed;
            SetRoomDraggingDisabled(editorActive);
            if (!editorActive)
            {
                EndPaintIfNeeded();
                _selectionMarquee.Cancel();
                UpdateOverlays(visible: false);
                _hoverInfoPanel.UpdateInfo(visible: false, _regionId, roomName: null);
                return;
            }

            HandleKeyboardShortcuts();
            UpdateStateLabels();
            UpdateOverlays(visible: true);
            RoomPanel hoveredRoom = HoveredRoomPanel();
            _hoverInfoPanel.UpdateInfo(
                visible: true,
                _regionId,
                hoveredRoom?.roomRep?.room?.name);
            BringEditorUiToFront(this);
            HandleMapInput();
        }

        public override void ClearSprites()
        {
            EndPaintIfNeeded();
            _selectionMarquee.Cancel();
            SetRoomDraggingDisabled(false);
            WeatherSpatialPreview.Clear();
            base.ClearSprites();
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            if (type != DevUISignalType.ButtonClick || sender == null)
            {
                return;
            }

            switch (sender.IDstring)
            {
                case "TargetPrev":
                    ChangeTarget(-1);
                    break;
                case "TargetNext":
                case "Target":
                    ChangeTarget(1);
                    break;
                case "Overview":
                    _overview = !_overview;
                    EndPaintIfNeeded();
                    break;
                case "Default":
                    CycleDefault();
                    break;
                case "Brush":
                    _brush = NextBrush(_brush);
                    break;
                case "ApplySelected":
                    ApplyRuleToSelection(_brush, "Apply brush to selection");
                    break;
                case "PreviewMinus":
                    SetPreviewIntensity(_previewIntensity - 0.10f);
                    break;
                case "PreviewPlus":
                    SetPreviewIntensity(_previewIntensity + 0.10f);
                    break;
                case "Preview":
                    TogglePreview();
                    break;
                case "SelectAll":
                    SelectAll();
                    break;
                case "SelectNone":
                    _selection.Clear();
                    break;
                case "SelectInvert":
                    InvertSelection();
                    break;
                case "SelectSubregion":
                    SelectCurrentSubregion();
                    break;
                case "SelectConnected":
                    SelectConnected();
                    break;
                case "SelectShelters":
                    SelectBy(room => room.shelter);
                    break;
                case "SelectGates":
                    SelectBy(room => room.gate);
                    break;
                case "SelectOffscreen":
                    SelectBy(room => room.offScreenDen);
                    break;
                case "StopGate":
                    _stopAtGates = !_stopAtGates;
                    break;
                case "StopSubregion":
                    _stopAtSubregion = !_stopAtSubregion;
                    break;
                case "ForceAllow":
                    ApplyRuleToSelection(WeatherSpatialRule.Allow, "Allow selected rooms");
                    break;
                case "ForceDeny":
                    ApplyRuleToSelection(WeatherSpatialRule.Deny, "Deny selected rooms");
                    break;
                case "ForceInherit":
                    ApplyRuleToSelection(WeatherSpatialRule.Inherit, "Clear selected overrides");
                    break;
                case "Undo":
                    Undo();
                    break;
                case "Redo":
                    Redo();
                    break;
                case "Validate":
                    RunValidation();
                    break;
                case "Save":
                    Save();
                    break;
                case "Repair":
                    Repair();
                    break;
            }

            UpdateStateLabels();
        }

        private Button AddButton(string id, float x, float y, float width, string text)
        {
            Button button = new(owner, id, this, new Vector2(x, y), width, text);
            subNodes.Add(button);
            return button;
        }

        private DevUILabel AddLabel(string id, float x, float y, float width, string text)
        {
            DevUILabel label = new(owner, id, this, new Vector2(x, y), width, text);
            label.spriteColor = new Color(0f, 0f, 0f);
            label.textColor = new Color(1f, 1f, 1f);
            subNodes.Add(label);
            return label;
        }

        private void HandleKeyboardShortcuts()
        {
            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!control)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.S))
            {
                Save();
            }
            else if (Input.GetKeyDown(KeyCode.Z))
            {
                Undo();
            }
            else if (Input.GetKeyDown(KeyCode.Y))
            {
                Redo();
            }
        }

        private void HandleMapInput()
        {
            if (_overview || owner == null)
            {
                EndPaintIfNeeded();
                _selectionMarquee.Cancel();
                return;
            }

            if (_selectionMarquee.Active)
            {
                owner.draggedNode = this;
                _selectionMarquee.MoveTo(owner.mousePos);
                if (!Input.GetMouseButton(0))
                {
                    int count = _selectionMarquee.Complete(_roomPanels, _selection);
                    _status = "Box selected " + count + " room(s).";
                    if (ReferenceEquals(owner.draggedNode, this))
                    {
                        owner.draggedNode = null;
                    }
                }
                EndPaintIfNeeded();
                return;
            }

            if (MouseOver || dragged)
            {
                EndPaintIfNeeded();
                return;
            }

            if (Input.GetKey(KeyCode.Space))
            {
                EndPaintIfNeeded();
                return;
            }

            RoomPanel hovered = HoveredRoomPanel();
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift && Input.GetMouseButtonDown(0))
            {
                _selectionMarquee.Begin(owner.mousePos);
                owner.draggedNode = this;
                EndPaintIfNeeded();
                return;
            }

            // Shift+RMB is handled once by WeatherSpatialTogglePaintRuntime and
            // toggles the complete marquee selection. Do not start a paint stroke.
            if (shift)
            {
                EndPaintIfNeeded();
                return;
            }

            if (owner.mouseClick && hovered != null)
            {
                BeginPainting();
            }

            if (_painting && owner.mouseDown)
            {
                owner.draggedNode = this;
                if (hovered != null)
                {
                    PaintRoom(hovered.roomRep.room.name);
                }
            }
            else if (_painting && !owner.mouseDown)
            {
                EndPaintIfNeeded();
            }
            else if (owner.mouseDown)
            {
                // Weather edit mode reserves ordinary left-drag for painting. Hold
                // Space while dragging to hand control back to MapPage panning.
                owner.draggedNode = this;
            }
        }

        private void BeginPainting()
        {
            if (_painting)
            {
                return;
            }
            _painting = true;
            _paintTouched.Clear();
            _paintCommand = new EditCommand { Label = "Paint " + CurrentTarget.DisplayName };
        }

        private void PaintRoom(string roomName)
        {
            if (!_painting || string.IsNullOrEmpty(roomName) || !_paintTouched.Add(roomName))
            {
                return;
            }

            WeatherSpatialTarget target = CurrentTarget;
            if (!WeatherSpatialRegistry.CanSetRoomRule(_regionId, roomName, target, _brush))
            {
                SetFamilyPrerequisiteStatus(roomName, target);
                return;
            }

            WeatherSpatialRule before = WeatherSpatialRegistry.GetRoomRule(_regionId, roomName, target);
            if (before == _brush)
            {
                return;
            }
            if (!WeatherSpatialRegistry.SetRoomRule(_regionId, roomName, target, _brush))
            {
                SetFamilyPrerequisiteStatus(roomName, target);
                return;
            }
            _paintCommand.Changes.Add(new RuleChange(
                _regionId,
                roomName,
                target,
                before,
                _brush,
                isDefault: false));
        }

        private void EndPaintIfNeeded()
        {
            if (!_painting)
            {
                return;
            }
            _painting = false;
            _paintTouched.Clear();
            if (_paintCommand != null && _paintCommand.Changes.Count > 0)
            {
                PushUndo(_paintCommand);
            }
            _paintCommand = null;
        }

        private void ChangeTarget(int delta)
        {
            EndPaintIfNeeded();
            int count = WeatherSpatialCatalog.AllTargets.Count;
            if (count <= 0)
            {
                return;
            }
            _targetIndex = (_targetIndex + delta) % count;
            if (_targetIndex < 0)
            {
                _targetIndex += count;
            }
            RefreshPreviewTarget();
        }

        private void CycleDefault()
        {
            WeatherSpatialTarget target = CurrentTarget;
            WeatherSpatialRule before = WeatherSpatialRegistry.GetDefaultRule(_regionId, target);
            WeatherSpatialRule after = NextDefault(before);
            WeatherSpatialRegistry.SetDefaultRule(_regionId, target, after);
            EditCommand command = new() { Label = "Change region default" };
            command.Changes.Add(new RuleChange(
                _regionId,
                null,
                target,
                before,
                after,
                isDefault: true));
            PushUndo(command);
        }

        private void ApplyRuleToSelection(WeatherSpatialRule rule, string label)
        {
            if (_selection.Count == 0)
            {
                _status = "No rooms selected.";
                return;
            }

            WeatherSpatialTarget target = CurrentTarget;
            EditCommand command = new() { Label = label };
            int prerequisiteSkipped = 0;
            foreach (string roomName in _selection)
            {
                if (!WeatherSpatialRegistry.CanSetRoomRule(_regionId, roomName, target, rule))
                {
                    prerequisiteSkipped++;
                    continue;
                }

                WeatherSpatialRule before = WeatherSpatialRegistry.GetRoomRule(_regionId, roomName, target);
                if (before == rule)
                {
                    continue;
                }
                if (!WeatherSpatialRegistry.SetRoomRule(_regionId, roomName, target, rule))
                {
                    prerequisiteSkipped++;
                    continue;
                }
                command.Changes.Add(new RuleChange(
                    _regionId,
                    roomName,
                    target,
                    before,
                    rule,
                    isDefault: false));
            }
            if (command.Changes.Count > 0)
            {
                PushUndo(command);
            }

            if (prerequisiteSkipped > 0)
            {
                if (WeatherSpatialCatalog.TryGetFamily(target.Kind, target.WeatherId, out WeatherSpatialFamily family))
                {
                    _status = prerequisiteSkipped + " room(s) skipped: allow [Family] " + family.Id + " first.";
                }
                else
                {
                    _status = prerequisiteSkipped + " room(s) skipped: parent family is not allowed.";
                }
            }
        }

        private void SetFamilyPrerequisiteStatus(
            string roomName,
            in WeatherSpatialTarget target)
        {
            if (WeatherSpatialCatalog.TryGetFamily(target.Kind, target.WeatherId, out WeatherSpatialFamily family))
            {
                _status = roomName + ": allow [Family] " + family.Id + " before editing " + target.DisplayName + ".";
            }
            else
            {
                _status = roomName + ": parent family is not allowed.";
            }
        }

        private void PushUndo(EditCommand command)
        {
            if (command == null || command.Changes.Count == 0)
            {
                return;
            }
            _undo.Add(command);
            if (_undo.Count > 128)
            {
                _undo.RemoveAt(0);
            }
            _redo.Clear();
            _status = command.Label;
        }

        private void Undo()
        {
            EndPaintIfNeeded();
            if (_undo.Count == 0)
            {
                _status = "Nothing to undo.";
                return;
            }
            EditCommand command = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            for (int i = command.Changes.Count - 1; i >= 0; i--)
            {
                ApplyChange(command.Changes[i], useAfter: false);
            }
            _redo.Add(command);
            _status = "Undo: " + command.Label;
        }

        private void Redo()
        {
            EndPaintIfNeeded();
            if (_redo.Count == 0)
            {
                _status = "Nothing to redo.";
                return;
            }
            EditCommand command = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            for (int i = 0; i < command.Changes.Count; i++)
            {
                ApplyChange(command.Changes[i], useAfter: true);
            }
            _undo.Add(command);
            _status = "Redo: " + command.Label;
        }

        private static void ApplyChange(RuleChange change, bool useAfter)
        {
            WeatherSpatialRule rule = useAfter ? change.After : change.Before;
            if (change.IsDefault)
            {
                WeatherSpatialRegistry.SetDefaultRule(change.RegionId, change.Target, rule);
            }
            else
            {
                WeatherSpatialRegistry.SetRoomRule(
                    change.RegionId,
                    change.RoomName,
                    change.Target,
                    rule);
            }
        }

        private void TogglePreview()
        {
            _previewActive = !_previewActive;
            if (_previewActive)
            {
                RefreshPreviewTarget();
            }
            else
            {
                WeatherSpatialPreview.Clear();
            }
        }

        private void SetPreviewIntensity(float value)
        {
            _previewIntensity = Mathf.Clamp(value, 0.05f, 1f);
            if (_previewActive)
            {
                RefreshPreviewTarget();
            }
        }

        private void RefreshPreviewTarget()
        {
            if (!_previewActive)
            {
                return;
            }
            WeatherSpatialMember preview = WeatherSpatialCatalog.PreviewFor(CurrentTarget);
            if (string.IsNullOrEmpty(preview.Id))
            {
                WeatherSpatialPreview.Clear();
                _previewActive = false;
                return;
            }
            WeatherSpatialPreview.Set(_world, preview.Kind, preview.Id, _previewIntensity);
        }

        private void SelectAll()
        {
            _selection.Clear();
            for (int i = 0; i < _roomPanels.Count; i++)
            {
                if (_roomPanels[i].roomRep?.room != null)
                {
                    _selection.Add(_roomPanels[i].roomRep.room.name);
                }
            }
        }

        private void InvertSelection()
        {
            HashSet<string> inverted = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _roomPanels.Count; i++)
            {
                string roomName = _roomPanels[i].roomRep?.room?.name;
                if (!string.IsNullOrEmpty(roomName) && !_selection.Contains(roomName))
                {
                    inverted.Add(roomName);
                }
            }
            _selection.Clear();
            foreach (string roomName in inverted)
            {
                _selection.Add(roomName);
            }
        }

        private void SelectCurrentSubregion()
        {
            AbstractRoom anchor = ResolveAnchorRoom();
            if (anchor == null)
            {
                _status = "No anchor room for subregion selection.";
                return;
            }
            string subregion = anchor.subregionName ?? string.Empty;
            _selection.Clear();
            for (int i = 0; i < _world.NumberOfRooms; i++)
            {
                AbstractRoom room = _world.GetAbstractRoom(_world.firstRoomIndex + i);
                if (room != null && string.Equals(room.subregionName ?? string.Empty, subregion, StringComparison.Ordinal))
                {
                    _selection.Add(room.name);
                }
            }
        }

        private void SelectConnected()
        {
            AbstractRoom anchor = ResolveAnchorRoom();
            if (anchor == null)
            {
                _status = "No anchor room for connected selection.";
                return;
            }

            string anchorSubregion = anchor.subregionName ?? string.Empty;
            Queue<AbstractRoom> queue = new();
            HashSet<int> visited = new();
            _selection.Clear();
            queue.Enqueue(anchor);
            visited.Add(anchor.index);

            while (queue.Count > 0)
            {
                AbstractRoom current = queue.Dequeue();
                _selection.Add(current.name);

                if (_stopAtGates && current.gate && current.index != anchor.index)
                {
                    continue;
                }

                int[] connections = current.connections;
                if (connections == null)
                {
                    continue;
                }
                for (int i = 0; i < connections.Length; i++)
                {
                    int index = connections[i];
                    if (index < 0 || !visited.Add(index))
                    {
                        continue;
                    }
                    AbstractRoom next = _world.GetAbstractRoom(index);
                    if (next == null)
                    {
                        continue;
                    }
                    if (_stopAtSubregion &&
                        !string.Equals(next.subregionName ?? string.Empty, anchorSubregion, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    _selection.Add(next.name);
                    if (!(_stopAtGates && next.gate))
                    {
                        queue.Enqueue(next);
                    }
                }
            }
        }

        private void SelectBy(Func<AbstractRoom, bool> predicate)
        {
            _selection.Clear();
            for (int i = 0; i < _world.NumberOfRooms; i++)
            {
                AbstractRoom room = _world.GetAbstractRoom(_world.firstRoomIndex + i);
                if (room != null && predicate(room))
                {
                    _selection.Add(room.name);
                }
            }
        }

        private AbstractRoom ResolveAnchorRoom()
        {
            RoomPanel hovered = HoveredRoomPanel();
            if (hovered?.roomRep?.room != null)
            {
                return hovered.roomRep.room;
            }

            if (owner?.game?.cameras != null && owner.game.cameras.Length > 0 &&
                owner.game.cameras[0]?.room?.abstractRoom != null)
            {
                return owner.game.cameras[0].room.abstractRoom;
            }

            foreach (string roomName in _selection)
            {
                AbstractRoom room = _world.GetAbstractRoom(roomName);
                if (room != null)
                {
                    return room;
                }
            }
            return null;
        }

        private RoomPanel HoveredRoomPanel()
        {
            for (int i = _roomPanels.Count - 1; i >= 0; i--)
            {
                RoomPanel panel = _roomPanels[i];
                if (panel.Visible && panel.miniMap != null && panel.miniMap.MouseOver)
                {
                    return panel;
                }
            }
            return null;
        }

        private void Save()
        {
            EndPaintIfNeeded();
            if (WeatherSpatialRegistry.Save())
            {
                _status = "Saved.";
                RunValidation();
            }
            else
            {
                _status = "Save failed. See log.";
            }
        }

        private void Repair()
        {
            EndPaintIfNeeded();
            if (string.IsNullOrEmpty(WeatherSpatialRegistry.FatalLoadError))
            {
                _status = "JSON is not in a fatal state; repair not required.";
                return;
            }
            if (WeatherSpatialRegistry.RepairBrokenFile())
            {
                _undo.Clear();
                _redo.Clear();
                _selection.Clear();
                _status = "Repaired WeatherSpatial.json.";
                RunValidation();
            }
            else
            {
                _status = "Repair failed. See log.";
            }
        }

        private void RunValidation()
        {
            _lastValidation = WeatherSpatialRegistry.Validate(_world);
            _status = _lastValidation.ErrorCount == 0 && _lastValidation.WarningCount == 0
                ? "Validation: clean."
                : $"Validation: {_lastValidation.ErrorCount} error(s), {_lastValidation.WarningCount} warning(s).";
        }

        private void UpdateStateLabels()
        {
            WeatherSpatialTarget target = CurrentTarget;
            _targetButton.Text = target.DisplayName;
            _overviewButton.Text = "Overview: " + (_overview ? "ON" : "OFF");
            _defaultButton.Text = "Region Default: " + RuleName(WeatherSpatialRegistry.GetDefaultRule(_regionId, target));
            _brushButton.Text = "Brush: " + RuleName(_brush);
            _previewButton.Text = "Preview: " + (_previewActive ? "ON" : "OFF");
            _previewLabel.Text = Mathf.RoundToInt(_previewIntensity * 100f) + "%";
            _selectionLabel.Text = "Selected: " + _selection.Count;
            _stopGateButton.Text = "Stop Gates: " + (_stopAtGates ? "ON" : "OFF");
            _stopSubregionButton.Text = "Stop Subregion: " + (_stopAtSubregion ? "ON" : "OFF");
            _statusLabel.Text = (WeatherSpatialRegistry.Dirty ? "* " : string.Empty) + _status;
            _pathLabel.Text = ShortPath(WeatherSpatialRegistry.LoadedPath);

            for (int i = 0; i < _issueLabels.Length; i++)
            {
                string text = string.Empty;
                if (_lastValidation != null &&
                    i == _issueLabels.Length - 1 &&
                    _lastValidation.Issues.Count > _issueLabels.Length)
                {
                    text = "+ " + (_lastValidation.Issues.Count - (_issueLabels.Length - 1)) + " more issue(s)";
                }
                else if (_lastValidation != null && i < _lastValidation.Issues.Count)
                {
                    text = Truncate(_lastValidation.Issues[i].ToString(), 62);
                }
                _issueLabels[i].Text = text;
            }
        }

        private void UpdateOverlays(bool visible)
        {
            WeatherSpatialTarget target = CurrentTarget;
            foreach (KeyValuePair<RoomPanel, WeatherRoomOverlay> pair in _overlays)
            {
                RoomPanel panel = pair.Key;
                WeatherRoomOverlay overlay = pair.Value;
                string roomName = panel.roomRep?.room?.name ?? string.Empty;
                bool selected = _selection.Contains(roomName);
                overlay.UpdateVisual(
                    visible,
                    _overview,
                    _regionId,
                    target,
                    selected);
            }
        }

        private void SetRoomDraggingDisabled(bool disabled)
        {
            for (int i = 0; i < _roomPanels.Count; i++)
            {
                _roomPanels[i].disableDragging = disabled;
            }
        }

        private static void BringEditorUiToFront(DevUINode node)
        {
            if (node == null || node is WeatherRoomOverlay || node is MapRoomMarquee)
            {
                return;
            }
            for (int i = 0; i < node.fSprites.Count; i++)
            {
                node.fSprites[i]?.MoveToFront();
            }
            for (int i = 0; i < node.fLabels.Count; i++)
            {
                node.fLabels[i]?.MoveToFront();
            }
            for (int i = 0; i < node.subNodes.Count; i++)
            {
                BringEditorUiToFront(node.subNodes[i]);
            }
        }

        private static WeatherSpatialRule NextBrush(WeatherSpatialRule rule)
        {
            return rule == WeatherSpatialRule.Allow
                ? WeatherSpatialRule.Deny
                : rule == WeatherSpatialRule.Deny
                    ? WeatherSpatialRule.Inherit
                    : WeatherSpatialRule.Allow;
        }

        private static WeatherSpatialRule NextDefault(WeatherSpatialRule rule)
        {
            return rule == WeatherSpatialRule.Inherit
                ? WeatherSpatialRule.Allow
                : rule == WeatherSpatialRule.Allow
                    ? WeatherSpatialRule.Deny
                    : WeatherSpatialRule.Inherit;
        }

        private static string RuleName(WeatherSpatialRule rule)
        {
            return rule == WeatherSpatialRule.Allow
                ? "ALLOW"
                : rule == WeatherSpatialRule.Deny
                    ? "DENY"
                    : "INHERIT";
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "Path: unresolved";
            }
            string normalized = path.Replace('\\', '/');
            int world = normalized.LastIndexOf("/world/", StringComparison.OrdinalIgnoreCase);
            return "Path: " + (world >= 0 ? normalized.Substring(world + 1) : normalized);
        }

        private static string Truncate(string text, int length)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= length)
            {
                return text ?? string.Empty;
            }
            return text.Substring(0, Math.Max(0, length - 3)) + "...";
        }
    }

    private sealed class WeatherRoomHoverInfoPanel : Panel
    {
        private const int RowsPerColumn = 6;

        private static readonly Color AllowedColor = new(0.45f, 0.95f, 0.55f);
        private static readonly Color DeniedColor = new(1f, 0.42f, 0.35f);
        private static readonly Color MutedColor = new(0.72f, 0.72f, 0.72f);

        private readonly DevUILabel _roomLabel;
        private readonly DevUILabel[] _familyRows = new DevUILabel[RowsPerColumn];
        private readonly DevUILabel[] _weatherRows = new DevUILabel[RowsPerColumn];
        private readonly DevUILabel[] _dangerRows = new DevUILabel[RowsPerColumn];

        internal WeatherRoomHoverInfoPanel(
            DevInterface.DevUI owner,
            DevUINode parent)
            : base(
                owner,
                "DryCycle_Weather_Room_Hover_Info",
                parent,
                new Vector2(0f, -157f),
                new Vector2(310f, 154f),
                "Hovered Room Weather")
        {
            _roomLabel = AddInfoLabel(
                "HoverRoom",
                8f,
                122f,
                294f,
                "Room: <hover a room>",
                0.85f,
                Color.white);
            AddInfoLabel(
                "HoverLegend",
                8f,
                107f,
                294f,
                "A/D effective | R room  Z region  G global | +/- rule",
                0.68f,
                MutedColor);

            AddInfoLabel("HoverFamilyHeader", 8f, 91f, 88f, "FamWeather", 0.75f, Color.white);
            AddInfoLabel("HoverWeatherHeader", 101f, 91f, 99f, "SubWeather", 0.75f, Color.white);
            AddInfoLabel("HoverDangerHeader", 205f, 91f, 97f, "DangerType", 0.75f, Color.white);

            for (int i = 0; i < RowsPerColumn; i++)
            {
                float y = 77f - i * 13f;
                _familyRows[i] = AddInfoLabel(
                    "HoverFamily" + i,
                    8f,
                    y,
                    88f,
                    string.Empty,
                    0.68f,
                    MutedColor);
                _weatherRows[i] = AddInfoLabel(
                    "HoverWeather" + i,
                    101f,
                    y,
                    99f,
                    string.Empty,
                    0.68f,
                    MutedColor);
                _dangerRows[i] = AddInfoLabel(
                    "HoverDanger" + i,
                    205f,
                    y,
                    97f,
                    string.Empty,
                    0.68f,
                    MutedColor);
            }
        }

        internal void UpdateInfo(bool visible, string regionId, string roomName)
        {
            SetPanelVisible(visible);
            if (!visible)
            {
                return;
            }

            ClearRows(_familyRows);
            ClearRows(_weatherRows);
            ClearRows(_dangerRows);

            if (string.IsNullOrWhiteSpace(roomName))
            {
                _roomLabel.Text = "Room: <hover a room>";
                return;
            }

            _roomLabel.Text = "Room: " + roomName;
            int familyIndex = 0;
            int weatherIndex = 0;
            int dangerIndex = 0;
            IReadOnlyList<WeatherSpatialTarget> targets = WeatherSpatialCatalog.AllTargets;
            for (int i = 0; i < targets.Count; i++)
            {
                WeatherSpatialTarget target = targets[i];
                if (target.IsFamily)
                {
                    SetRow(_familyRows, ref familyIndex, regionId, roomName, target);
                }
                else if (target.Kind == WeatherScheduleEventKind.DangerType)
                {
                    SetRow(_dangerRows, ref dangerIndex, regionId, roomName, target);
                }
                else
                {
                    SetRow(_weatherRows, ref weatherIndex, regionId, roomName, target);
                }
            }
        }

        private DevUILabel AddInfoLabel(
            string id,
            float x,
            float y,
            float width,
            string text,
            float scale,
            Color color)
        {
            DevUILabel label = new(owner, id, this, new Vector2(x, y), width, text);
            label.spriteColor = Color.black;
            label.textColor = color;
            for (int i = 0; i < label.fLabels.Count; i++)
            {
                label.fLabels[i].scale = scale;
            }
            subNodes.Add(label);
            return label;
        }

        private static void ClearRows(DevUILabel[] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i].Text = string.Empty;
                rows[i].textColor = MutedColor;
            }
        }

        private static void SetRow(
            DevUILabel[] rows,
            ref int index,
            string regionId,
            string roomName,
            in WeatherSpatialTarget target)
        {
            if (index >= rows.Length)
            {
                return;
            }

            bool allowed = target.IsFamily
                ? WeatherSpatialRegistry.IsFamilyAllowed(regionId, roomName, target.FamilyId)
                : WeatherSpatialRegistry.IsAllowed(regionId, roomName, target.Kind, target.WeatherId);
            string rule = ResolvedRuleCode(regionId, roomName, target);
            string chance = ChanceText(regionId, target);
            string name = target.IsFamily ? target.FamilyId : target.WeatherId;

            DevUILabel row = rows[index++];
            row.Text = (allowed ? "A " : "D ") + rule + " " + name + " " + chance;
            row.textColor = allowed ? AllowedColor : DeniedColor;
        }

        private static string ResolvedRuleCode(
            string regionId,
            string roomName,
            in WeatherSpatialTarget target)
        {
            WeatherSpatialRule roomRule = WeatherSpatialRegistry.GetRoomRule(regionId, roomName, target);
            if (roomRule != WeatherSpatialRule.Inherit)
            {
                return "R" + RuleSign(roomRule);
            }

            WeatherSpatialRule regionRule = WeatherSpatialRegistry.GetDefaultRule(regionId, target);
            if (regionRule != WeatherSpatialRule.Inherit)
            {
                return "Z" + RuleSign(regionRule);
            }

            if (target.IsFamily)
            {
                return "G" + RuleSign(WeatherSpatialRegistry.GlobalDefault);
            }

            return "--";
        }

        private static string RuleSign(WeatherSpatialRule rule) =>
            rule == WeatherSpatialRule.Allow ? "+" : "-";

        private static string ChanceText(string regionId, in WeatherSpatialTarget target)
        {
            bool found = target.IsFamily
                ? WeatherSpatialRegistry.TryGetFamilyWeatherChance(regionId, target, out float chance)
                : WeatherSpatialRegistry.TryGetSubWeatherChance(regionId, target, out chance);
            return found ? Mathf.RoundToInt(chance) + "%" : "--";
        }

        private void SetPanelVisible(bool visible)
        {
            SetNodeVisible(this, visible);
        }

        private static void SetNodeVisible(DevUINode node, bool visible)
        {
            for (int i = 0; i < node.fSprites.Count; i++)
            {
                node.fSprites[i].isVisible = visible;
            }
            for (int i = 0; i < node.fLabels.Count; i++)
            {
                node.fLabels[i].isVisible = visible;
            }
            for (int i = 0; i < node.subNodes.Count; i++)
            {
                SetNodeVisible(node.subNodes[i], visible);
            }
        }
    }

    private sealed class WeatherRoomOverlay : DevUINode
    {
        private readonly RoomPanel _panel;
        private readonly FSprite _fill;
        private readonly FSprite[] _border = new FSprite[4];
        private readonly FLabel _label;

        internal WeatherRoomOverlay(
            DevInterface.DevUI owner,
            DevUINode parent,
            RoomPanel panel)
            : base(owner, "WeatherOverlay_" + (panel?.roomRep?.room?.name ?? "unknown"), parent)
        {
            _panel = panel;
            _fill = new FSprite("pixel")
            {
                anchorX = 0f,
                anchorY = 0f,
                isVisible = false
            };
            fSprites.Add(_fill);
            if (owner != null)
            {
                Futile.stage.AddChild(_fill);
            }

            for (int i = 0; i < _border.Length; i++)
            {
                _border[i] = new FSprite("pixel") { anchorX = 0f, anchorY = 0f, isVisible = false };
                fSprites.Add(_border[i]);
                if (owner != null)
                {
                    Futile.stage.AddChild(_border[i]);
                }
            }

            _label = new FLabel(RWCustom.Custom.GetFont(), string.Empty)
            {
                anchorX = 0f,
                anchorY = 1f,
                isVisible = false
            };
            fLabels.Add(_label);
            if (owner != null)
            {
                Futile.stage.AddChild(_label);
            }
        }

        internal void UpdateVisual(
            bool editorVisible,
            bool overview,
            string regionId,
            in WeatherSpatialTarget target,
            bool selected)
        {
            bool visible = editorVisible &&
                           _panel != null &&
                           !_panel.collapsed &&
                           _panel.Visible &&
                           _panel.miniMap != null &&
                           _panel.roomRep?.room != null;
            if (!visible)
            {
                SetVisible(false);
                return;
            }

            Vector2 pos = _panel.miniMap.absPos;
            Vector2 size = _panel.miniMap.size;
            size.x = Mathf.Max(2f, size.x);
            size.y = Mathf.Max(2f, size.y);
            string roomName = _panel.roomRep.room.name;

            _fill.x = pos.x;
            _fill.y = pos.y;
            _fill.scaleX = size.x;
            _fill.scaleY = size.y;

            if (overview)
            {
                _fill.color = new Color(0f, 0f, 0f);
                _fill.alpha = 0.08f;
                _label.text = OverviewText(regionId, roomName);
                _label.color = new Color(1f, 1f, 1f);
            }
            else
            {
                bool allowed = target.IsFamily
                    ? WeatherSpatialRegistry.IsFamilyAllowed(regionId, roomName, target.FamilyId)
                    : WeatherSpatialRegistry.IsAllowed(regionId, roomName, target.Kind, target.WeatherId);
                WeatherSpatialRule explicitRule = WeatherSpatialRegistry.GetRoomRule(regionId, roomName, target);
                _fill.color = allowed
                    ? new Color(0.15f, 0.85f, 0.55f)
                    : new Color(0.95f, 0.30f, 0.20f);
                _fill.alpha = allowed ? 0.28f : 0.24f;
                _label.text = explicitRule == WeatherSpatialRule.Allow
                    ? "A"
                    : explicitRule == WeatherSpatialRule.Deny
                        ? "D"
                        : "·";
                _label.color = new Color(1f, 1f, 1f);
            }

            _label.x = pos.x + 2f;
            _label.y = pos.y + size.y - 1f;
            SetBorder(pos, size, selected);
            _fill.isVisible = true;
            _label.isVisible = true;
        }

        private void SetBorder(Vector2 pos, Vector2 size, bool selected)
        {
            Color color = selected ? new Color(1f, 1f, 0.25f) : new Color(1f, 1f, 1f);
            float alpha = selected ? 0.95f : 0.35f;
            float thickness = selected ? 2f : 1f;

            for (int i = 0; i < _border.Length; i++)
            {
                _border[i].color = color;
                _border[i].alpha = alpha;
                _border[i].isVisible = true;
            }

            _border[0].x = pos.x;
            _border[0].y = pos.y;
            _border[0].scaleX = size.x;
            _border[0].scaleY = thickness;

            _border[1].x = pos.x;
            _border[1].y = pos.y + size.y - thickness;
            _border[1].scaleX = size.x;
            _border[1].scaleY = thickness;

            _border[2].x = pos.x;
            _border[2].y = pos.y;
            _border[2].scaleX = thickness;
            _border[2].scaleY = size.y;

            _border[3].x = pos.x + size.x - thickness;
            _border[3].y = pos.y;
            _border[3].scaleX = thickness;
            _border[3].scaleY = size.y;
        }

        private void SetVisible(bool visible)
        {
            _fill.isVisible = visible;
            _label.isVisible = visible;
            for (int i = 0; i < _border.Length; i++)
            {
                _border[i].isVisible = visible;
            }
        }

        private static string OverviewText(string regionId, string roomName)
        {
            IReadOnlyList<WeatherSpatialFamily> families = WeatherSpatialCatalog.AllFamilies;
            string text = string.Empty;
            for (int i = 0; i < families.Count; i++)
            {
                WeatherSpatialFamily family = families[i];
                bool allowed = false;
                for (int j = 0; j < family.Members.Count; j++)
                {
                    WeatherSpatialMember member = family.Members[j];
                    if (WeatherSpatialRegistry.IsAllowed(regionId, roomName, member.Kind, member.Id))
                    {
                        allowed = true;
                        break;
                    }
                }
                if (i > 0)
                {
                    text += " ";
                }
                text += allowed ? family.Id.Substring(0, 1).ToUpperInvariant() : "-";
            }
            return text;
        }
    }
}
