using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal enum CartographyRenderPass { UnderRooms, Content, Guides }

// A render layer is derived presentation data, never an extra layer written into the author file.
// Keeping this order in the scene makes the canvas, flat exports and layered exports agree.
internal sealed class CartographyRenderLayer
{
    internal string LayerId, Name;
    internal CartographyRenderPass Pass;
    internal CartographySceneNode[] Nodes;
    internal bool Accepts(CartographyPrimitive shape) => Pass == CartographyRenderPass.Guides
        ? shape.GuideOnly : !shape.GuideOnly && shape.UnderRooms == (Pass == CartographyRenderPass.UnderRooms);
}

internal static class CartographyComposition
{
    internal static CartographyRenderLayer[] Build(CartographyDocument document, IEnumerable<CartographySceneNode> nodes)
    {
        var byLayer = nodes.ToLookup(n => n.LayerId);
        var layers = document.Layers.Where(l => l.Visible && l.Opacity > 0 && byLayer.Contains(l.Id)).ToList();

        // The original DryCycle projects placed the built-in connection layer below every room.
        // Cornifer draws room bodies before connection cores. Resolve that presentation order
        // for existing projects too, without rewriting any saved layers or moving authored items.
        var links = layers.Find(l => l.Id == "links");
        var lastRoom = layers.LastOrDefault(l => byLayer[l.Id].Any(n => n.Room));
        if (links != null && lastRoom != null && layers.IndexOf(links) < layers.IndexOf(lastRoom))
        {
            layers.Remove(links);
            layers.Insert(layers.IndexOf(lastRoom) + 1, links);
        }

        var result = new List<CartographyRenderLayer>();
        foreach (var pass in new[] { CartographyRenderPass.UnderRooms, CartographyRenderPass.Content, CartographyRenderPass.Guides })
            foreach (var layer in layers)
            {
                var batch = new CartographyRenderLayer
                {
                    LayerId = layer.Id, Pass = pass,
                    Name = layer.Name + (pass == CartographyRenderPass.UnderRooms ? " / Borders" : "")
                };
                // Keep route passes available while a live drag changes diagonal/axis alignment.
                batch.Nodes = byLayer[layer.Id].Where(n => n.FromId != null || n.Primitives.Any(batch.Accepts)).ToArray();
                if (batch.Nodes.Length > 0) result.Add(batch);
            }
        return result.ToArray();
    }
}
