using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI;

/// <summary>
/// Shared Shift+LMB room-selection marquee for MapPage authoring tools.
/// Selection is replaced with every visible room whose minimap rectangle intersects
/// the dragged screen-space rectangle.
/// </summary>
internal sealed class MapRoomMarquee : DevUINode
{
    private readonly FSprite _fill;
    private readonly FSprite[] _border = new FSprite[4];
    private Vector2 _start;
    private Vector2 _current;

    internal bool Active { get; private set; }

    internal MapRoomMarquee(DevInterface.DevUI owner, string id, DevUINode parent)
        : base(owner, id, parent)
    {
        _fill = new FSprite("pixel")
        {
            anchorX = 0f,
            anchorY = 0f,
            color = new Color(0.25f, 0.62f, 1f),
            alpha = 0.13f,
            isVisible = false
        };
        fSprites.Add(_fill);
        if (owner != null)
        {
            Futile.stage.AddChild(_fill);
        }

        for (int i = 0; i < _border.Length; i++)
        {
            _border[i] = new FSprite("pixel")
            {
                anchorX = 0f,
                anchorY = 0f,
                color = new Color(0.35f, 0.72f, 1f),
                alpha = 0.95f,
                isVisible = false
            };
            fSprites.Add(_border[i]);
            if (owner != null)
            {
                Futile.stage.AddChild(_border[i]);
            }
        }
    }

    internal void Begin(Vector2 position)
    {
        Active = true;
        _start = position;
        _current = position;
        RefreshGeometry();
        SetVisible(true);
    }

    internal void MoveTo(Vector2 position)
    {
        if (!Active)
        {
            return;
        }

        _current = position;
        RefreshGeometry();
    }

    internal int Complete(
        IReadOnlyList<RoomPanel> roomPanels,
        HashSet<string> selection)
    {
        if (!Active || roomPanels == null || selection == null)
        {
            Cancel();
            return 0;
        }

        Rect marquee = SelectionRect();
        selection.Clear();
        for (int i = 0; i < roomPanels.Count; i++)
        {
            RoomPanel panel = roomPanels[i];
            if (panel == null ||
                !panel.Visible ||
                panel.collapsed ||
                panel.miniMap == null ||
                panel.roomRep?.room == null)
            {
                continue;
            }

            Vector2 roomPos = panel.miniMap.absPos;
            Vector2 roomSize = panel.miniMap.size;
            Rect roomRect = new(
                roomPos.x,
                roomPos.y,
                Mathf.Max(1f, roomSize.x),
                Mathf.Max(1f, roomSize.y));
            if (IntersectsInclusive(marquee, roomRect))
            {
                selection.Add(panel.roomRep.room.name);
            }
        }

        int count = selection.Count;
        Cancel();
        return count;
    }

    internal void Cancel()
    {
        Active = false;
        SetVisible(false);
    }

    private Rect SelectionRect()
    {
        float left = Mathf.Min(_start.x, _current.x);
        float bottom = Mathf.Min(_start.y, _current.y);
        return new Rect(
            left,
            bottom,
            Mathf.Abs(_current.x - _start.x),
            Mathf.Abs(_current.y - _start.y));
    }

    private void RefreshGeometry()
    {
        Rect rect = SelectionRect();
        const float thickness = 2f;
        _fill.x = rect.xMin;
        _fill.y = rect.yMin;
        _fill.scaleX = Mathf.Max(1f, rect.width);
        _fill.scaleY = Mathf.Max(1f, rect.height);

        SetGeometry(_border[0], rect.xMin, rect.yMin, Mathf.Max(1f, rect.width), thickness);
        SetGeometry(_border[1], rect.xMin, rect.yMax - thickness, Mathf.Max(1f, rect.width), thickness);
        SetGeometry(_border[2], rect.xMin, rect.yMin, thickness, Mathf.Max(1f, rect.height));
        SetGeometry(_border[3], rect.xMax - thickness, rect.yMin, thickness, Mathf.Max(1f, rect.height));
    }

    private static bool IntersectsInclusive(Rect a, Rect b)
    {
        return a.xMin <= b.xMax &&
               a.xMax >= b.xMin &&
               a.yMin <= b.yMax &&
               a.yMax >= b.yMin;
    }

    private static void SetGeometry(FSprite sprite, float x, float y, float width, float height)
    {
        sprite.x = x;
        sprite.y = y;
        sprite.scaleX = width;
        sprite.scaleY = height;
    }

    private void SetVisible(bool visible)
    {
        _fill.isVisible = visible;
        for (int i = 0; i < _border.Length; i++)
        {
            _border[i].isVisible = visible;
        }
    }
}
