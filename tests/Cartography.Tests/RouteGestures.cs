using System;
using System.Linq;
using DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class Program
{
    private static void RouteGestures()
    {
        var source = Source(); var original = Annotated();
        var route = original.Items.First(i => i.Kind == CartographyItemKind.Connection);
        var scene = CartographySceneBuilder.Build(original, source);
        var node = scene.Nodes.First(n => n.Id == route.Id);
        float x = (node.FromX + node.ToX) / 2, y = (node.FromY + node.ToY) / 2;
        int segment = CartographyRouteEditing.NearestSegment(node.Points, x, y, 8, out var hit);
        Check(segment == 0 && hit != null, "A route without bends can be edited by clicking its actual segment.");
        var edit = CartographyRouteEditing.Manual(route, node);
        edit.Points.Insert(segment, hit);
        edit.Points[segment] = CartographyRouteEditing.Drag(hit.X + 34, hit.Y - 57, hit, false);
        var changed = CartographyEditing.Apply(original, source, new CartographyCommand { Kind = CartographyCommandKind.UpdateItem, Item = edit });
        var result = CartographySceneBuilder.Build(changed, source).Nodes.First(n => n.Id == route.Id);
        Check(result.Points.Length == node.Points.Length + 1 && Math.Abs(result.Points[1].X - edit.Points[0].X) < .001f && Math.Abs(result.Points[1].Y - edit.Points[0].Y) < .001f, "Dragging a new bend commits free-position geometry without grid snapping.");
        Check(result.FromX == node.FromX && result.ToY == node.ToY && route.Points.Count == 0, "Manual routing retains live source ports and leaves the original snapshot unchanged.");
        string saved = CartographyStorage.Serialize(changed);
        var restored = CartographyStorage.Deserialize(saved, changed.Identity);
        Check(CartographyEditing.SameItem(edit, restored.Items.Find(i => i.Id == edit.Id)), "The actual edited route survives save/reopen, including its mode and bends.");
        var moved = CartographyEditing.Apply(changed, source, new CartographyCommand { Kind = CartographyCommandKind.Move, Ids = new[] { node.FromId }, X = 50, Y = -25 });
        var movedRoute = CartographySceneBuilder.Build(moved, source).Nodes.First(n => n.Id == route.Id);
        Check(movedRoute.FromX == result.FromX + 50 && movedRoute.FromY == result.FromY - 25 && movedRoute.Points[1].X == result.Points[1].X,
            "Moving a room updates its port while retaining manually authored bends.");
        var points = new[] { new CartographyPoint { X = 0 }, new CartographyPoint { X = 10 }, new CartographyPoint { X = 20 }, new CartographyPoint { X = 30 } };
        Check(CartographyRouteEditing.NearestHandle(points, 19, 0, 225) == 1, "At 4% zoom overlapping handle hit areas choose the nearest bend, not the first bend.");
        Check(CartographyRouteEditing.NearestHandle(new[] { points[0], points[3] }, 0, 0, 8) == -1, "Room endpoints are not editable author handles.");
        var horizontal = CartographyRouteEditing.Drag(50, 17, new CartographyPoint { X = 3, Y = 5 }, true);
        Check(horizontal.X == 50 && horizontal.Y == 5, "Shift constrains against the original handle without snapping the free axis.");
        var diagonal = new[] { new CartographyPoint(), new CartographyPoint { X = 100, Y = 100 } };
        Check(CartographyRouteEditing.NearestSegment(diagonal, 0, 100, 8, out _) < 0, "Empty space inside a diagonal connection's bounding box is not a line hit.");
        Check(CartographyRouteEditing.NearestSegment(new[] { points[0], points[0] }, 1, 0, 8, out var zero) == 0 && zero.X == 0,
            "Collapsed source endpoints can be rerouted without division by zero.");
        var remove = edit.Clone(); remove.Points.RemoveAt(0);
        var deleted = CartographyEditing.Apply(changed, source, new CartographyCommand { Kind = CartographyCommandKind.UpdateItem, Item = remove });
        Check(deleted.Items.Find(i => i.Id == route.Id).Visible && deleted.Items.Find(i => i.Id == route.Id).Points.Count == 0,
            "Deleting the selected bend does not delete the connection.");
        var automatic = route.Clone(); automatic.Appearance.Route = CartographyRouteMode.HorizontalFirst;
        var automaticNode = CartographySceneBuilder.Route(original, source, automatic, original.Layer(automatic.LayerId));
        var converted = CartographyRouteEditing.Manual(automatic, automaticNode);
        Check(converted.Points.Count == automaticNode.Points.Length - 2 && converted.Appearance.Route == CartographyRouteMode.Manual,
            "Editing an automatically routed line preserves existing intermediate bends.");
    }
}
