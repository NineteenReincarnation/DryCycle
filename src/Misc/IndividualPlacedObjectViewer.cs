using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Standalone placed-object visibility manager for Rain World's Objects dev-tools page.
/// It mirrors the workflow of RegionKit's individual-object mode without requiring RegionKit.
/// </summary>
internal static class IndividualPlacedObjectViewer
{
    private const string SwitchModeButtonId = "DC_Switch_Object_View_Mode";
    private const string PanelId = "DC_Placed_Objects_Panel";

    private const string ObjectButtonPrefix = "DC_Placed_Object_";
    private const string ToggleButtonPrefix = "DC_Toggle_Object_";
    private const string DuplicateButtonPrefix = "DC_Duplicate_Object_";
    private const string TypeButtonPrefix = "DC_View_Type_";

    private const string SelectAllButtonId = "DC_Select_All_Objects";
    private const string DeselectAllButtonId = "DC_Deselect_All_Objects";
    private const string DeleteSelectedButtonId = "DC_Delete_Selected_Objects";
    private const string ConfirmDeleteButtonId = "DC_Confirm_Delete_Selected_Objects";
    private const string CancelDeleteButtonId = "DC_Cancel_Delete_Selected_Objects";
    private const string SortButtonId = "DC_Sort_Placed_Objects";
    private const string PreviousObjectPageButtonId = "DC_Previous_Object_Page";
    private const string NextObjectPageButtonId = "DC_Next_Object_Page";
    private const string PreviousTypePageButtonId = "DC_Previous_Type_Page";
    private const string NextTypePageButtonId = "DC_Next_Type_Page";

    private static readonly ConditionalWeakTable<ObjectsPage, PageState> PageStates =
        new ConditionalWeakTable<ObjectsPage, PageState>();

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.ObjectsPage.ctor += ObjectsPage_ctor;
        On.DevInterface.ObjectsPage.Signal += ObjectsPage_Signal;
        On.DevInterface.ObjectsPage.RemoveObject += ObjectsPage_RemoveObject;
        On.DevInterface.ObjectsPage.CreateObjRep += ObjectsPage_CreateObjRep;
        On.DevInterface.PlacedObjectRepresentation.Update += PlacedObjectRepresentation_Update;

        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.ObjectsPage.ctor -= ObjectsPage_ctor;
        On.DevInterface.ObjectsPage.Signal -= ObjectsPage_Signal;
        On.DevInterface.ObjectsPage.RemoveObject -= ObjectsPage_RemoveObject;
        On.DevInterface.ObjectsPage.CreateObjRep -= ObjectsPage_CreateObjRep;
        On.DevInterface.PlacedObjectRepresentation.Update -= PlacedObjectRepresentation_Update;

        _enabled = false;
    }

    private static PageState State(ObjectsPage page)
    {
        if (!PageStates.TryGetValue(page, out PageState state))
        {
            state = new PageState();
            PageStates.Add(page, state);
        }

        return state;
    }

    private static void ObjectsPage_ctor(
        On.DevInterface.ObjectsPage.orig_ctor orig,
        ObjectsPage self,
        DevUI owner,
        string IDstring,
        DevUINode parentNode,
        string name)
    {
        orig(self, owner, IDstring, parentNode, name);

        // Same location used by the familiar RegionKit workflow: directly below
        // the Objects page selector in the top dev-tools navigation area.
        self.subNodes.Add(new Button(
            owner,
            SwitchModeButtonId,
            self,
            new Vector2(180f, 710f),
            110f,
            "Switch mode"));
    }

    private static void ObjectsPage_Signal(
        On.DevInterface.ObjectsPage.orig_Signal orig,
        ObjectsPage self,
        DevUISignalType type,
        DevUINode sender,
        string message)
    {
        // Let vanilla process Save/Create/etc. first. DryCycle's controls all use
        // private IDs, so vanilla simply ignores those button signals.
        orig(self, type, sender, message);

        PageState state = State(self);
        string id = sender == null ? string.Empty : sender.IDstring;

        if (id == SwitchModeButtonId)
        {
            state.IndividualMode = !state.IndividualMode;

            if (state.IndividualMode)
            {
                EnsurePanel(self, state);
            }
            else
            {
                RemovePanel(self, state);
            }

            state.Panel?.RefreshButtons();
            self.Refresh();
            return;
        }

        if (TryReadIndex(id, ObjectButtonPrefix, out int objectIndex))
        {
            if (IsValidObjectIndex(self, objectIndex))
            {
                state.VisibleIndexes.Clear();
                state.VisibleIndexes.Add(objectIndex);
                self.Refresh();
                state.Panel?.RefreshButtons();
            }
            return;
        }

        if (TryReadIndex(id, ToggleButtonPrefix, out objectIndex))
        {
            if (IsValidObjectIndex(self, objectIndex))
            {
                if (!state.VisibleIndexes.Remove(objectIndex))
                {
                    state.VisibleIndexes.Add(objectIndex);
                }

                self.Refresh();
                state.Panel?.RefreshButtons();
            }
            return;
        }

        if (TryReadIndex(id, DuplicateButtonPrefix, out objectIndex))
        {
            if (IsValidObjectIndex(self, objectIndex))
            {
                DuplicateObject(self, state, objectIndex);
            }
            return;
        }

        if (id == SelectAllButtonId)
        {
            state.VisibleIndexes.Clear();
            for (int i = 0; i < self.RoomSettings.placedObjects.Count; i++)
            {
                if (state.TypeFilter == null || self.RoomSettings.placedObjects[i].type == state.TypeFilter)
                {
                    state.VisibleIndexes.Add(i);
                }
            }

            self.Refresh();
            state.Panel?.RefreshButtons();
            return;
        }

        if (id == DeselectAllButtonId)
        {
            state.VisibleIndexes.Clear();
            self.Refresh();
            state.Panel?.RefreshButtons();
            return;
        }

        if (id == SortButtonId)
        {
            SortPlacedObjects(self, state);
            return;
        }

        if (id == DeleteSelectedButtonId)
        {
            if (state.VisibleIndexes.Count > 0)
            {
                DeleteConfirmationPanel confirmPanel = new DeleteConfirmationPanel(
                    self.owner,
                    "DC_Confirm_Delete_Panel",
                    self,
                    new Vector2(600f, 400f),
                    new Vector2(150f, 85f),
                    "Delete selected");

                self.subNodes.Add(confirmPanel);
                self.tempNodes.Add(confirmPanel);
            }
            return;
        }

        if (id == ConfirmDeleteButtonId)
        {
            DeleteSelectedObjects(self, state);
            return;
        }

        if (id == CancelDeleteButtonId)
        {
            self.Refresh();
            return;
        }

        if (id == PreviousObjectPageButtonId || id == NextObjectPageButtonId)
        {
            int pageCount = GetObjectPageCount(self, state.TypeFilter);
            state.ObjectPage += id == PreviousObjectPageButtonId ? -1 : 1;
            state.ObjectPage = WrapPage(state.ObjectPage, pageCount);
            state.Panel?.RefreshButtons();
            return;
        }

        if (id == PreviousTypePageButtonId || id == NextTypePageButtonId)
        {
            int pageCount = GetTypePageCount(self);
            state.TypePage += id == PreviousTypePageButtonId ? -1 : 1;
            state.TypePage = WrapPage(state.TypePage, pageCount);
            state.Panel?.RefreshButtons();
            return;
        }

        if (id.StartsWith(TypeButtonPrefix, StringComparison.Ordinal))
        {
            string typeName = id.Substring(TypeButtonPrefix.Length);
            state.TypeFilter = typeName == "ALL" ? null : new PlacedObject.Type(typeName);
            state.ObjectPage = 0;
            state.Panel?.RefreshButtons();
            return;
        }

        // Vanilla has already created the new PlacedObject by this point. In
        // individual mode, automatically select the freshly placed object so it
        // does not disappear as soon as the page refreshes.
        if (type == DevUISignalType.Create && state.IndividualMode && self.RoomSettings.placedObjects.Count > 0)
        {
            int newIndex = self.RoomSettings.placedObjects.Count - 1;
            if (!state.VisibleIndexes.Contains(newIndex))
            {
                state.VisibleIndexes.Add(newIndex);
            }

            state.Panel?.RefreshButtons();
            self.Refresh();
        }
    }

    private static void ObjectsPage_CreateObjRep(
        On.DevInterface.ObjectsPage.orig_CreateObjRep orig,
        ObjectsPage self,
        PlacedObject.Type tp,
        PlacedObject pObj)
    {
        PageState state = State(self);

        if (state.IndividualMode && pObj != null)
        {
            int index = self.RoomSettings.placedObjects.IndexOf(pObj);
            if (index >= 0 && !state.VisibleIndexes.Contains(index))
            {
                return;
            }
        }

        orig(self, tp, pObj);
    }

    private static void ObjectsPage_RemoveObject(
        On.DevInterface.ObjectsPage.orig_RemoveObject orig,
        ObjectsPage self,
        PlacedObjectRepresentation objRep)
    {
        PageState state = State(self);
        int removedIndex = self.RoomSettings.placedObjects.IndexOf(objRep.pObj);

        if (removedIndex >= 0)
        {
            ReindexSelectionAfterRemoval(state.VisibleIndexes, removedIndex);
        }

        orig(self, objRep);
        ClampPages(self, state);
        state.Panel?.RefreshButtons();
    }

    private static void PlacedObjectRepresentation_Update(
        On.DevInterface.PlacedObjectRepresentation.orig_Update orig,
        PlacedObjectRepresentation self)
    {
        orig(self);

        ObjectsPage page = self.Page as ObjectsPage;
        if (page == null || !State(page).IndividualMode || self.fLabels.Count == 0)
        {
            return;
        }

        string label = self.fLabels[0].text ?? string.Empty;
        int firstSpace = label.IndexOf(' ');
        string firstToken = firstSpace < 0 ? label : label.Substring(0, firstSpace);
        if (int.TryParse(firstToken, out _))
        {
            return;
        }

        int index = self.RoomSettings.placedObjects.IndexOf(self.pObj);
        if (index >= 0)
        {
            self.fLabels[0].text = index + " " + label;
        }
    }

    private static void EnsurePanel(ObjectsPage page, PageState state)
    {
        if (state.Panel != null)
        {
            return;
        }

        state.Panel = new PlacedObjectsPanel(
            page,
            page.owner,
            PanelId,
            page,
            new Vector2(1050f, 40f),
            new Vector2(300f, 620f),
            "Placed Objects");

        page.subNodes.Add(state.Panel);
    }

    private static void RemovePanel(ObjectsPage page, PageState state)
    {
        if (state.Panel == null)
        {
            return;
        }

        state.Panel.ClearSprites();
        page.subNodes.Remove(state.Panel);
        state.Panel = null;
    }

    private static void DuplicateObject(ObjectsPage page, PageState state, int index)
    {
        PlacedObject original = page.RoomSettings.placedObjects[index];
        string[] serialized = original.ToString().Split(new[] { "><" }, StringSplitOptions.None);
        if (serialized.Length < 4)
        {
            return;
        }

        PlacedObject clone = new PlacedObject(PlacedObject.Type.None, null);
        clone.FromString(serialized);
        clone.pos = page.owner.game.cameras[0].pos + new Vector2(200f, 200f);
        page.RoomSettings.placedObjects.Add(clone);

        int cloneIndex = page.RoomSettings.placedObjects.Count - 1;
        if (!state.VisibleIndexes.Contains(cloneIndex))
        {
            state.VisibleIndexes.Add(cloneIndex);
        }

        ClampPages(page, state);
        state.Panel?.RefreshButtons();
        page.Refresh();
    }

    private static void SortPlacedObjects(ObjectsPage page, PageState state)
    {
        List<PlacedObject> selectedObjects = new List<PlacedObject>();
        for (int i = 0; i < state.VisibleIndexes.Count; i++)
        {
            int index = state.VisibleIndexes[i];
            if (IsValidObjectIndex(page, index))
            {
                selectedObjects.Add(page.RoomSettings.placedObjects[index]);
            }
        }

        page.RoomSettings.placedObjects.Sort((a, b) =>
            string.Compare(a.type.ToString(), b.type.ToString(), StringComparison.Ordinal));

        state.VisibleIndexes.Clear();
        for (int i = 0; i < selectedObjects.Count; i++)
        {
            int newIndex = page.RoomSettings.placedObjects.IndexOf(selectedObjects[i]);
            if (newIndex >= 0)
            {
                state.VisibleIndexes.Add(newIndex);
            }
        }

        ClampPages(page, state);
        state.Panel?.RefreshButtons();
        page.Refresh();
    }

    private static void DeleteSelectedObjects(ObjectsPage page, PageState state)
    {
        List<PlacedObject> toDelete = new List<PlacedObject>();
        for (int i = 0; i < state.VisibleIndexes.Count; i++)
        {
            int index = state.VisibleIndexes[i];
            if (IsValidObjectIndex(page, index))
            {
                toDelete.Add(page.RoomSettings.placedObjects[index]);
            }
        }

        for (int i = 0; i < toDelete.Count; i++)
        {
            page.RoomSettings.placedObjects.Remove(toDelete[i]);
        }

        state.VisibleIndexes.Clear();
        ClampPages(page, state);
        state.Panel?.RefreshButtons();
        page.Refresh();
    }

    private static void ReindexSelectionAfterRemoval(List<int> indexes, int removedIndex)
    {
        for (int i = indexes.Count - 1; i >= 0; i--)
        {
            if (indexes[i] == removedIndex)
            {
                indexes.RemoveAt(i);
            }
            else if (indexes[i] > removedIndex)
            {
                indexes[i]--;
            }
        }
    }

    private static void ClampPages(ObjectsPage page, PageState state)
    {
        state.ObjectPage = WrapPage(state.ObjectPage, GetObjectPageCount(page, state.TypeFilter));
        state.TypePage = WrapPage(state.TypePage, GetTypePageCount(page));
    }

    private static int GetObjectPageCount(ObjectsPage page, PlacedObject.Type filter)
    {
        int count = 0;
        for (int i = 0; i < page.RoomSettings.placedObjects.Count; i++)
        {
            if (filter == null || page.RoomSettings.placedObjects[i].type == filter)
            {
                count++;
            }
        }

        return Math.Max(1, (count + PlacedObjectsPanel.MaxPlacedObjectsPerPage - 1) /
            PlacedObjectsPanel.MaxPlacedObjectsPerPage);
    }

    private static int GetTypePageCount(ObjectsPage page)
    {
        int itemCount = GetRoomTypes(page).Count + 1; // + All
        return Math.Max(1, (itemCount + PlacedObjectsPanel.MaxTypesPerPage - 1) /
            PlacedObjectsPanel.MaxTypesPerPage);
    }

    private static int WrapPage(int page, int pageCount)
    {
        if (pageCount <= 0)
        {
            return 0;
        }

        page %= pageCount;
        return page < 0 ? page + pageCount : page;
    }

    private static List<PlacedObject.Type> GetRoomTypes(ObjectsPage page)
    {
        List<PlacedObject.Type> result = new List<PlacedObject.Type>();
        for (int i = 0; i < page.RoomSettings.placedObjects.Count; i++)
        {
            PlacedObject.Type type = page.RoomSettings.placedObjects[i].type;
            if (!result.Contains(type))
            {
                result.Add(type);
            }
        }

        result.Sort((a, b) => string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal));
        return result;
    }

    private static bool TryReadIndex(string id, string prefix, out int index)
    {
        index = -1;
        return id.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(id.Substring(prefix.Length), out index);
    }

    private static bool IsValidObjectIndex(ObjectsPage page, int index)
    {
        return index >= 0 && index < page.RoomSettings.placedObjects.Count;
    }

    private sealed class PageState
    {
        public PlacedObjectsPanel Panel;
        public bool IndividualMode;
        public readonly List<int> VisibleIndexes = new List<int>();
        public int ObjectPage;
        public int TypePage;
        public PlacedObject.Type TypeFilter;
    }

    private sealed class PlacedObjectsPanel : Panel, IDevUISignals
    {
        internal const int MaxPlacedObjectsPerPage = 16;
        internal const int MaxTypesPerPage = 8;

        private readonly ObjectsPage _objectsPage;
        private readonly List<DevUINode> _dynamicNodes = new List<DevUINode>();

        public PlacedObjectsPanel(
            ObjectsPage objectsPage,
            DevUI owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            Vector2 size,
            string title)
            : base(owner, IDstring, parentNode, pos, size, title)
        {
            _objectsPage = objectsPage;

            subNodes.Add(new DevUILabel(owner, "DC_Type_Filter_Label", this,
                new Vector2(5f, size.y - 20f), 290f, "View by type"));

            subNodes.Add(new Button(owner, PreviousTypePageButtonId, this,
                new Vector2(5f, size.y - 40f), 100f, "Previous Page"));
            subNodes.Add(new Button(owner, NextTypePageButtonId, this,
                new Vector2(195f, size.y - 40f), 100f, "Next Page"));

            subNodes.Add(new DevUILabel(owner, "DC_Placed_Object_Label", this,
                new Vector2(5f, size.y - 240f), 290f, "Placed Objects"));

            subNodes.Add(new Button(owner, PreviousObjectPageButtonId, this,
                new Vector2(5f, size.y - 260f), 100f, "Previous Page"));
            subNodes.Add(new Button(owner, NextObjectPageButtonId, this,
                new Vector2(195f, size.y - 260f), 100f, "Next Page"));

            subNodes.Add(new Button(owner, SelectAllButtonId, this,
                new Vector2(5f, size.y - 300f), 70f, "Select All"));
            subNodes.Add(new Button(owner, DeselectAllButtonId, this,
                new Vector2(80f, size.y - 300f), 70f, "Deselect All"));
            subNodes.Add(new Button(owner, SortButtonId, this,
                new Vector2(155f, size.y - 300f), 35f, "Sort"));
            subNodes.Add(new Button(owner, DeleteSelectedButtonId, this,
                new Vector2(195f, size.y - 300f), 100f, "Delete Selected"));

            RefreshButtons();
        }

        public void RefreshButtons()
        {
            ClearDynamicNodes();
            PageState state = State(_objectsPage);
            ClampPages(_objectsPage, state);
            BuildTypeButtons(state);
            BuildObjectButtons(state);
        }

        private void BuildTypeButtons(PageState state)
        {
            List<PlacedObject.Type> roomTypes = GetRoomTypes(_objectsPage);
            int totalItems = roomTypes.Count + 1;
            int start = state.TypePage * MaxTypesPerPage;
            int end = Math.Min(start + MaxTypesPerPage, totalItems);

            for (int itemIndex = start; itemIndex < end; itemIndex++)
            {
                bool isAll = itemIndex == 0;
                PlacedObject.Type type = isAll ? null : roomTypes[itemIndex - 1];
                string typeName = isAll ? "ALL" : type.ToString();
                bool active = isAll ? state.TypeFilter == null : state.TypeFilter == type;
                string text = (active ? "> " : "  ") + (isAll ? "All" : typeName);
                int row = itemIndex - start;

                AddDynamic(new Button(
                    owner,
                    TypeButtonPrefix + typeName,
                    this,
                    new Vector2(5f, size.y - 80f - 20f * row),
                    290f,
                    text));
            }
        }

        private void BuildObjectButtons(PageState state)
        {
            List<int> filteredIndexes = new List<int>();
            for (int i = 0; i < RoomSettings.placedObjects.Count; i++)
            {
                if (state.TypeFilter == null || RoomSettings.placedObjects[i].type == state.TypeFilter)
                {
                    filteredIndexes.Add(i);
                }
            }

            int start = state.ObjectPage * MaxPlacedObjectsPerPage;
            int end = Math.Min(start + MaxPlacedObjectsPerPage, filteredIndexes.Count);

            for (int listIndex = start; listIndex < end; listIndex++)
            {
                int objectIndex = filteredIndexes[listIndex];
                int row = listIndex - start;
                float y = size.y - 320f - 20f * row;

                AddDynamic(new Button(
                    owner,
                    ObjectButtonPrefix + objectIndex,
                    this,
                    new Vector2(5f, y),
                    184f,
                    objectIndex + " " + RoomSettings.placedObjects[objectIndex].type));

                AddDynamic(new Button(
                    owner,
                    DuplicateButtonPrefix + objectIndex,
                    this,
                    new Vector2(194f, y),
                    80f,
                    "Duplicate"));

                string toggleText = state.VisibleIndexes.Contains(objectIndex) ? " -" : " +";
                AddDynamic(new Button(
                    owner,
                    ToggleButtonPrefix + objectIndex,
                    this,
                    new Vector2(279f, y),
                    16f,
                    toggleText));
            }
        }

        private void AddDynamic(DevUINode node)
        {
            subNodes.Add(node);
            _dynamicNodes.Add(node);
        }

        private void ClearDynamicNodes()
        {
            for (int i = _dynamicNodes.Count - 1; i >= 0; i--)
            {
                _dynamicNodes[i].ClearSprites();
                subNodes.Remove(_dynamicNodes[i]);
            }
            _dynamicNodes.Clear();
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            _objectsPage.Signal(type, sender, message);
        }
    }

    private sealed class DeleteConfirmationPanel : Panel, IDevUISignals
    {
        public DeleteConfirmationPanel(
            DevUI owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            Vector2 size,
            string title)
            : base(owner, IDstring, parentNode, pos, size, title)
        {
            subNodes.Add(new DevUILabel(owner, "DC_Delete_Question", this,
                new Vector2(5f, size.y - 20f), 140f, "Are you sure?"));
            subNodes.Add(new Button(owner, ConfirmDeleteButtonId, this,
                new Vector2(5f, size.y - 60f), 140f, "Yes - delete"));
            subNodes.Add(new Button(owner, CancelDeleteButtonId, this,
                new Vector2(5f, size.y - 80f), 140f, "Cancel"));
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            ObjectsPage page = parentNode as ObjectsPage;
            page?.Signal(type, sender, message);
        }
    }
}
