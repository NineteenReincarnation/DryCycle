using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Final transaction gate for Render Map output. Composition/preflight prevents semantic omissions;
/// this validator catches damaged/truncated PNG staging files, malformed metadata, duplicate room
/// records and rectangles outside the encoded PNG before the atomic pair replaces known-good output.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapIncrementalRenderPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapRenderOutputValidatorPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.RenderOutputValidator";
    public const string PluginName = "DryCycle Player Map Render Output Validator";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapRenderOutputValidator.Enable(Logger);
    private void OnDisable() => PlayerMapRenderOutputValidator.Disable();
}

internal static class PlayerMapRenderOutputValidator
{
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map staged-output validation enabled through direct commit call; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static void ValidateBeforeCommit(string pngPath, string metadataPath)
    {
        if (enabled)
            ValidatePair(pngPath, metadataPath);
    }

    private static void ValidatePair(string pngPath, string metadataPath)
    {
        if (!File.Exists(pngPath)) throw new IOException("Staged PNG is missing.");
        if (!File.Exists(metadataPath)) throw new IOException("Staged map_image metadata is missing.");

        ReadPngHeaderAndStructure(pngPath, out int width, out int height);
        ValidateMetadata(metadataPath, width, height);
    }

    private static void ReadPngHeaderAndStructure(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 33) throw new InvalidDataException("Staged PNG is too small to contain IHDR/IEND.");

        byte[] signature = new byte[8];
        ReadExact(stream, signature, 0, signature.Length);
        for (int i = 0; i < PngSignature.Length; i++)
            if (signature[i] != PngSignature[i]) throw new InvalidDataException("Staged output is not a PNG file.");

        bool sawHeader = false;
        bool sawImageData = false;
        bool sawEnd = false;
        while (stream.Position < stream.Length)
        {
            uint length = ReadUInt32BigEndian(stream);
            if (length > int.MaxValue) throw new InvalidDataException("PNG chunk length is unsafe.");
            byte[] type = new byte[4];
            ReadExact(stream, type, 0, 4);
            string chunkType = Encoding.ASCII.GetString(type);
            long remaining = stream.Length - stream.Position;
            if (remaining < (long)length + 4L)
                throw new InvalidDataException("PNG chunk " + chunkType + " is truncated.");

            if (chunkType == "IHDR")
            {
                if (sawHeader || length != 13) throw new InvalidDataException("PNG IHDR is malformed or duplicated.");
                byte[] header = new byte[13];
                ReadExact(stream, header, 0, 13);
                width = unchecked((int)ReadUInt32BigEndian(header, 0));
                height = unchecked((int)ReadUInt32BigEndian(header, 4));
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                    throw new InvalidDataException("PNG dimensions are invalid: " + width + "x" + height + ".");
                sawHeader = true;
            }
            else
            {
                if (chunkType == "IDAT" && length > 0) sawImageData = true;
                if (chunkType == "IEND")
                {
                    if (length != 0) throw new InvalidDataException("PNG IEND chunk is malformed.");
                    sawEnd = true;
                }
                stream.Seek(length, SeekOrigin.Current);
            }

            // Skip CRC. Structural validation here intentionally does not decode zlib data; the PNG
            // encoder itself generated that stream. Truncation/bounds errors are the failure class
            // that can leave a broken official pair after a filesystem or encoder fault.
            stream.Seek(4, SeekOrigin.Current);
            if (sawEnd) break;
        }

        if (!sawHeader || !sawImageData || !sawEnd)
            throw new InvalidDataException("PNG staging file is incomplete (IHDR/IDAT/IEND required).");
    }

    private static void ValidateMetadata(string path, int width, int height)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) throw new InvalidDataException("map_image metadata contains no rooms.");

        HashSet<string> rooms = new(StringComparer.OrdinalIgnoreCase);
        int records = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i]?.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException("Malformed map_image line " + (i + 1) + ".");

            string room = line.Substring(0, colon).Trim();
            if (!rooms.Add(room)) throw new InvalidDataException("Duplicate map_image room record: " + room + ".");
            string[] parts = line.Substring(colon + 1).Trim().Split(',');
            if (parts.Length != 4 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
                throw new InvalidDataException("Malformed map_image rectangle for " + room + ".");

            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height)
                throw new InvalidDataException(
                    "map_image rectangle for " + room + " exceeds PNG bounds: " +
                    x + "," + y + "," + w + "," + h + " vs " + width + "x" + height + ".");
            records++;
        }

        if (records == 0) throw new InvalidDataException("map_image metadata contains no valid room records.");
    }

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        byte[] buffer = new byte[4];
        ReadExact(stream, buffer, 0, 4);
        return ReadUInt32BigEndian(buffer, 0);
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

}
