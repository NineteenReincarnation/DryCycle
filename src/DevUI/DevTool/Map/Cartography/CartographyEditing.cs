using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal enum CartographyCommandKind
{
    Move, Add, UpdateItem, Delete, Duplicate, AssignLayer, Align,
    AddLayer, UpdateLayer, MoveLayer, DeleteLayer, Style, AddMissingRooms, Save, Export, Open
}

// A command names the document and revision observed by the frontend. A delayed release from an old
// region or an Undo during a drag must never apply positions to another revision of the document.
internal sealed class CartographyCommand
{
    internal string DocumentId = string.Empty;
    internal long Revision = 0;
    internal CartographyCommandKind Kind;
    internal string[] Ids = Array.Empty<string>();
    internal string LayerId;
    internal float X, Y;
    internal int Integer;
    internal CartographyItem Item;
    internal CartographyLayer Layer;
    internal CartographyDocument Style = null;
    internal string Path = string.Empty;
}

internal static class CartographyEditing
{
    internal static bool SameItem(CartographyItem a, CartographyItem b) => a != null && b != null &&
        a.Id == b.Id && a.Kind == b.Kind && a.LayerId == b.LayerId && a.Room == b.Room && a.Text == b.Text &&
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.Size == b.Size &&
        a.Stroke == b.Stroke && a.Color == b.Color && a.Visible == b.Visible && a.Marker == b.Marker;

    internal static bool SameLayer(CartographyLayer a, CartographyLayer b) => a != null && b != null &&
        a.Id == b.Id && a.Name == b.Name && a.Visible == b.Visible && a.Locked == b.Locked && a.Opacity == b.Opacity;

    internal static CartographyDocument Apply(CartographyDocument original, CartographySource source, CartographyCommand command)
    {
        CartographyDocument next = original.Clone();
        HashSet<string> ids = new(command.Ids ?? Array.Empty<string>(), StringComparer.Ordinal);
        List<CartographyItem> targets = next.Items.Where(item => ids.Contains(item.Id) && next.Editable(item)).ToList();
        switch (command.Kind)
        {
            case CartographyCommandKind.Move:
                foreach (CartographyItem item in targets) { item.X += command.X; item.Y += command.Y; }
                break;
            case CartographyCommandKind.Add:
                if (command.Item == null || command.Item.Kind == CartographyItemKind.Room) throw new InvalidOperationException("Choose an annotation tool.");
                RequireEditableLayer(next, command.Item.LayerId);
                next.Items.Add(command.Item.Clone());
                break;
            case CartographyCommandKind.UpdateItem:
                CartographyItem current = next.Items.Find(item => item.Id == command.Item?.Id);
                if (current == null || next.Layer(current.LayerId)?.Locked != false) throw new InvalidOperationException("The object is missing or its layer is locked.");
                if (current.Kind != command.Item.Kind || current.Room != command.Item.Room || current.LayerId != command.Item.LayerId)
                    throw new InvalidOperationException("An object's source identity cannot be changed by the inspector.");
                next.Items[next.Items.IndexOf(current)] = command.Item.Clone();
                break;
            case CartographyCommandKind.Delete:
                next.Items.RemoveAll(item => targets.Contains(item));
                break;
            case CartographyCommandKind.Duplicate:
                foreach (CartographyItem item in targets.Where(item => item.Kind != CartographyItemKind.Room))
                {
                    CartographyItem copy = item.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.X += 20; copy.Y += 20;
                    next.Items.Add(copy);
                }
                break;
            case CartographyCommandKind.AssignLayer:
                RequireEditableLayer(next, command.LayerId);
                foreach (CartographyItem item in targets) item.LayerId = command.LayerId;
                break;
            case CartographyCommandKind.Align:
                Align(targets, source, (CartographyAlignment)command.Integer);
                break;
            case CartographyCommandKind.AddLayer:
                next.Layers.Add(command.Layer?.Clone() ?? throw new InvalidOperationException("Missing layer."));
                break;
            case CartographyCommandKind.UpdateLayer:
                int index = next.Layers.FindIndex(layer => layer.Id == command.Layer?.Id);
                if (index < 0) throw new InvalidOperationException("The layer no longer exists.");
                next.Layers[index] = command.Layer.Clone();
                break;
            case CartographyCommandKind.MoveLayer:
                CartographyLayer moving = next.Layer(command.LayerId) ?? throw new InvalidOperationException("Missing layer.");
                int oldIndex = next.Layers.IndexOf(moving);
                next.Layers.RemoveAt(oldIndex);
                next.Layers.Insert(Math.Max(0, Math.Min(next.Layers.Count, oldIndex + command.Integer)), moving);
                break;
            case CartographyCommandKind.DeleteLayer:
                CartographyLayer deleting = next.Layer(command.LayerId);
                if (deleting == null || deleting.Id == "links" || deleting.Locked || next.Layers.Count <= 2)
                    throw new InvalidOperationException("Keep the connections layer and at least one object layer; unlock a layer before removing it.");
                CartographyLayer destination = next.Layers.FirstOrDefault(layer => layer.Id != deleting.Id && layer.Id != "links" && !layer.Locked && layer.Visible);
                if (destination == null) throw new InvalidOperationException("No visible unlocked layer can receive these objects.");
                foreach (CartographyItem item in next.Items.Where(item => item.LayerId == deleting.Id)) item.LayerId = destination.Id;
                next.Layers.Remove(deleting);
                break;
            case CartographyCommandKind.Style:
                CartographyDocument style = command.Style ?? throw new InvalidOperationException("Missing map style.");
                next.Title = style.Title; next.FontFamily = style.FontFamily;
                next.Background = style.Background; next.Terrain = style.Terrain; next.Water = style.Water; next.Connections = style.Connections;
                next.ShowRoomNames = style.ShowRoomNames; next.ShowConnections = style.ShowConnections;
                next.Transparent = style.Transparent; next.CropSolid = style.CropSolid; next.ExportScale = style.ExportScale; next.Padding = style.Padding;
                break;
            case CartographyCommandKind.AddMissingRooms:
                source.AddMissingRooms(next);
                break;
            default: throw new InvalidOperationException("This command does not edit the document.");
        }
        next.Validate(); // Validate before replacing the authoritative instance: failed edits are atomic.
        return next;
    }

    private static void RequireEditableLayer(CartographyDocument document, string id)
    {
        if (document.Layer(id) is not { Visible: true, Locked: false })
            throw new InvalidOperationException("Choose a visible, unlocked layer.");
    }

    private static void Align(List<CartographyItem> items, CartographySource source, CartographyAlignment alignment)
    {
        if (!Enum.IsDefined(typeof(CartographyAlignment), alignment)) throw new InvalidOperationException("Unknown alignment.");
        if (items.Count < 2) return;
        Dictionary<string, CartographyRect> bounds = items.ToDictionary(item => item.Id, item => CartographySceneBuilder.Bounds(item, source));
        CartographyRect total = bounds.Values.Aggregate(CartographyRect.Union);
        if (alignment == CartographyAlignment.DistributeX || alignment == CartographyAlignment.DistributeY)
        {
            if (items.Count < 3) return;
            bool horizontal = alignment == CartographyAlignment.DistributeX;
            items.Sort((a, b) => (horizontal ? bounds[a.Id].X : bounds[a.Id].Y).CompareTo(horizontal ? bounds[b.Id].X : bounds[b.Id].Y));
            float occupied = items.Sum(item => horizontal ? bounds[item.Id].Width : bounds[item.Id].Height);
            float gap = ((horizontal ? total.Width : total.Height) - occupied) / (items.Count - 1);
            float cursor = horizontal ? total.X : total.Y;
            foreach (CartographyItem item in items)
            {
                CartographyRect box = bounds[item.Id];
                if (horizontal) item.X += cursor - box.X; else item.Y += cursor - box.Y;
                cursor += (horizontal ? box.Width : box.Height) + gap;
            }
            return;
        }
        foreach (CartographyItem item in items)
        {
            CartographyRect box = bounds[item.Id];
            switch (alignment)
            {
                case CartographyAlignment.Left: item.X += total.X - box.X; break;
                case CartographyAlignment.CenterX: item.X += total.X + total.Width / 2 - box.X - box.Width / 2; break;
                case CartographyAlignment.Right: item.X += total.Right - box.Right; break;
                case CartographyAlignment.Top: item.Y += total.Y - box.Y; break;
                case CartographyAlignment.CenterY: item.Y += total.Y + total.Height / 2 - box.Y - box.Height / 2; break;
                case CartographyAlignment.Bottom: item.Y += total.Bottom - box.Bottom; break;
            }
        }
    }
}
