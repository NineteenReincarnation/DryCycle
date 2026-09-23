using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal sealed class CartographyRegionOption
{
    internal readonly string Code, Name, LocalizedName;
    internal CartographyRegionOption(string code, string name, string localizedName = null)
    {
        Code = code.ToUpperInvariant();
        Name = string.IsNullOrWhiteSpace(name) || name == "Unknown Region" ? Code : name.Trim();
        LocalizedName = string.IsNullOrWhiteSpace(localizedName) ? Name : localizedName.Trim();
    }
    internal string Label(bool localized) => Code + " · " + (localized ? LocalizedName : Name);
}

// A snapshot of paths selected by the game's asset resolver. Workers never enumerate active
// mods or touch Unity objects, and don't implement a second mod-precedence algorithm.
internal sealed class CartographyRegionFiles
{
    private readonly Dictionary<string, string> files;
    internal readonly string Region;

    private CartographyRegionFiles(string region, Dictionary<string, string> files)
    { Region = region.ToUpperInvariant(); this.files = files; }

    internal static CartographyRegionFiles Capture(string region, Func<string, string[]> listFiles)
    {
        region = region.Trim().ToLowerInvariant();
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { "world/" + region, "world/" + region + "-rooms", "world/gates" })
            foreach (string path in listFiles(folder))
                if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    files[folder + "/" + Path.GetFileName(path)] = Path.GetFullPath(path);
        return new CartographyRegionFiles(region, files);
    }

    internal string Resolve(string relative) => files.TryGetValue(relative.Replace('\\', '/'), out string path) ? path : null;
    internal string Read(string relative)
    {
        string path = Resolve(relative);
        return path == null ? null : File.ReadAllText(path);
    }

    // Re-evaluated on a selection/refresh, never per frame. A newly added override is included
    // by Capture; edits, removals and a changed resolved path invalidate the decoded source.
    internal string Fingerprint()
    {
        StringBuilder value = new();
        foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            FileInfo info = new(file.Value);
            value.Append(file.Key.ToLowerInvariant()).Append('|').Append(file.Value.ToLowerInvariant())
                .Append('|').Append(info.Exists ? info.Length : -1).Append('|')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return CartographyStorage.HashText(value.ToString());
    }

    internal static string StreamingAssetsRoot(string path)
    {
        string root = Path.GetFullPath(path);
        if (File.Exists(root)) root = Path.GetDirectoryName(root);
        if (!Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Equals("StreamingAssets", StringComparison.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(root, "RainWorld_Data", "StreamingAssets");
            if (!Directory.Exists(candidate)) candidate = Path.Combine(root, "StreamingAssets");
            if (!Directory.Exists(candidate)) throw new DirectoryNotFoundException("Rain World StreamingAssets not found / 找不到游戏资源目录: " + root);
            root = candidate;
        }
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("StreamingAssets not found / 资源目录不存在: " + root);
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static CartographyRegionOption[] Catalog(IEnumerable<string> directories, Func<string, string> resolve,
        Func<string, string> fullName, Func<string, string> translate)
    {
        return directories.Select(Path.GetFileName).Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(code => File.Exists(resolve("world/" + code + "/world_" + code + ".txt")))
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Select(code => { string name = fullName(code); return new CartographyRegionOption(code, name, translate(name)); })
            .ToArray();
    }
}

// Bounded, in-memory derived data only. No geometry or merged-mod content is written to author XML.
internal sealed class CartographySourceCache
{
    private sealed class Entry { internal string Fingerprint; internal CartographySource Source; internal long Used; }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly int capacity;
    private long clock;
    internal CartographySourceCache(int capacity = 4) { this.capacity = Math.Max(1, capacity); }
    internal int Count => entries.Count;

    // Called by one background loader at a time.
    internal CartographySource Load(string identity, CartographyRegionFiles files, string campaign, bool refresh, out bool hit)
    {
        string fingerprint = files.Fingerprint();
        hit = !refresh && entries.TryGetValue(identity, out Entry cached) && cached.Fingerprint == fingerprint;
        if (hit) { Entry entry = entries[identity]; entry.Used = ++clock; return entry.Source; }
        CartographySource source = CartographyRegionLoader.Load(files.Region, campaign, files.Read);
        if (files.Fingerprint() != fingerprint)
            throw new IOException("Region files changed during loading; refresh again / 区域文件在读取期间发生变化，请刷新重试。");
        entries[identity] = new Entry { Fingerprint = fingerprint, Source = source, Used = ++clock };
        while (entries.Count > capacity) entries.Remove(entries.OrderBy(pair => pair.Value.Used).First().Key);
        return source;
    }
}
