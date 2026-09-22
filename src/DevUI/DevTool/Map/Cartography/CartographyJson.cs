using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal static class CartographyJson
{
    internal static Dictionary<string, object> Parse(string text)
    {
        if (text.Length > 64 * 1024 * 1024) throw new InvalidDataException("JSON exceeds 64 MiB.");
        using XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(text), XmlDictionaryReaderQuotas.Max);
        return Read(XElement.Load(reader)) as Dictionary<string, object> ?? throw new InvalidDataException("Expected a JSON object.");
    }
    private static object Read(XElement e)
    {
        switch ((string)e.Attribute("type"))
        {
            case "object": return e.Elements().ToDictionary(c => (string)c.Attribute("item") ?? c.Name.LocalName, Read, StringComparer.Ordinal);
            case "array": return e.Elements().Select(Read).ToList();
            case "number": return double.Parse(e.Value, CultureInfo.InvariantCulture);
            case "boolean": return bool.Parse(e.Value);
            case "null": return null;
            default: return e.Value;
        }
    }
    internal static Dictionary<string, object> Object(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) && v is Dictionary<string, object> obj ? obj : new();
    internal static IEnumerable<Dictionary<string, object>> Objects(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out object v) && v is List<object> a ? a.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
    internal static string Text(this Dictionary<string, object> d, string key, string fallback = "") => d.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback;
    internal static float Number(this Dictionary<string, object> d, string key, float fallback = 0) => float.TryParse(d.Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    internal static bool Flag(this Dictionary<string, object> d, string key, bool fallback = false) => bool.TryParse(d.Text(key), out bool v) ? v : fallback;
}
