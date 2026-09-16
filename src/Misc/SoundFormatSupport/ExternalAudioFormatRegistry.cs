using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DryCycle.Misc.SoundFormatSupport;

internal enum ExternalAudioDecoderKind
{
    Unity = 0,
    MediaFoundation = 1
}

internal readonly struct ExternalAudioFormat
{
    internal ExternalAudioFormat(
        string extension,
        AudioType unityAudioType,
        ExternalAudioDecoderKind decoder,
        bool supportsUnityStreaming,
        int priority)
    {
        Extension = NormalizeExtension(extension);
        UnityAudioType = unityAudioType;
        Decoder = decoder;
        SupportsUnityStreaming = supportsUnityStreaming;
        Priority = priority;
    }

    internal string Extension { get; }
    internal AudioType UnityAudioType { get; }
    internal ExternalAudioDecoderKind Decoder { get; }
    internal bool SupportsUnityStreaming { get; }
    internal int Priority { get; }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("Audio extension cannot be empty.", nameof(extension));

        string value = extension.Trim();
        if (value[0] != '.') value = "." + value;
        return value.ToLowerInvariant();
    }
}

internal readonly struct ResolvedAudioFile
{
    internal ResolvedAudioFile(string path, ExternalAudioFormat format)
    {
        Path = path;
        Format = format;
    }

    internal string Path { get; }
    internal ExternalAudioFormat Format { get; }
}

/// <summary>
/// Single source of truth for loose audio formats understood by DryCycle's SoundLoader bridge.
/// Adding a future format belongs here; the SoundLoader hooks never need another extension branch.
/// </summary>
internal static class ExternalAudioFormatRegistry
{
    private const int MaxVariations = 100;

    private static readonly object Gate = new();
    private static readonly List<ExternalAudioFormat> Formats = new();
    private static readonly Dictionary<string, ExternalAudioFormat> ByExtension =
        new(StringComparer.OrdinalIgnoreCase);

    static ExternalAudioFormatRegistry()
    {
        // Keep the historical loose-file preference deterministic when duplicate formats exist.
        RegisterBuiltIn(new ExternalAudioFormat(".wav", (AudioType)20, ExternalAudioDecoderKind.Unity, true, 100));
        RegisterBuiltIn(new ExternalAudioFormat(".ogg", (AudioType)14, ExternalAudioDecoderKind.Unity, true, 90));
        RegisterBuiltIn(new ExternalAudioFormat(".mp3", (AudioType)13, ExternalAudioDecoderKind.Unity, true, 80));

        // Rain World's Unity generation reports AudioType.ACC as unsupported. M4A therefore uses
        // the Media Foundation backend on Windows and a Unity UNKNOWN-type fallback elsewhere.
        RegisterBuiltIn(new ExternalAudioFormat(".m4a", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 70));
    }

    internal static string[] SupportedExtensions
    {
        get
        {
            lock (Gate)
            {
                string[] result = new string[Formats.Count];
                for (int i = 0; i < Formats.Count; i++) result[i] = Formats[i].Extension;
                return result;
            }
        }
    }

    internal static bool RegisterFormat(ExternalAudioFormat format)
    {
        if (string.IsNullOrWhiteSpace(format.Extension)) return false;
        lock (Gate)
        {
            if (ByExtension.ContainsKey(format.Extension)) return false;
            AddFormatNoLock(format);
            return true;
        }
    }

    internal static bool IsSupported(string pathOrFileName) => TryGetFormat(pathOrFileName, out _);

    internal static bool TryGetFormat(string pathOrFileName, out ExternalAudioFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(pathOrFileName)) return false;

        string extension;
        try { extension = Path.GetExtension(pathOrFileName); }
        catch { return false; }
        if (string.IsNullOrEmpty(extension)) return false;

        lock (Gate)
            return ByExtension.TryGetValue(extension, out format);
    }

    internal static bool TryResolveSoundEffect(string logicalName, int oneBasedVariation, out ResolvedAudioFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(logicalName) || oneBasedVariation < 1) return false;

        string stem = logicalName.Trim();
        if (oneBasedVariation == 1)
        {
            // A numbered family wins over the unnumbered spelling. This prevents both Foo.wav and
            // Foo_1.mp3 from racing for allAudio[x].audio[0] when a mod accidentally ships both.
            if (TryResolveAssetStem("SoundEffects", stem + "_1", out file)) return true;
            return TryResolveAssetStem("SoundEffects", stem, out file);
        }

        return TryResolveAssetStem("SoundEffects", stem + "_" + oneBasedVariation, out file);
    }

    internal static int CountSoundEffectVariations(string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName)) return 0;
        string stem = logicalName.Trim();

        bool numberedFirst = TryResolveAssetStem("SoundEffects", stem + "_1", out _);
        if (!numberedFirst && !TryResolveAssetStem("SoundEffects", stem, out _)) return 0;

        int count = 1;
        for (int variation = 2; variation <= MaxVariations; variation++)
        {
            if (!TryResolveAssetStem("SoundEffects", stem + "_" + variation, out _)) break;
            count++;
        }
        return count;
    }

    internal static bool TryResolveLoadedSoundEffect(string selectedStem, out ResolvedAudioFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(selectedStem)) return false;
        return TryResolveOverrideStem("LoadedSoundEffects", selectedStem.Trim(), out file);
    }

    internal static bool TryResolveLoadedAmbient(string clipName, out ResolvedAudioFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(clipName)) return false;

        string relativeDirectory = "LoadedSoundEffects" + Path.DirectorySeparatorChar + "Ambient";
        string extension;
        try { extension = Path.GetExtension(clipName); }
        catch { return false; }

        if (!string.IsNullOrEmpty(extension) && TryGetFormat(clipName, out ExternalAudioFormat exactFormat))
            return TryResolveOverrideExact(relativeDirectory, clipName, exactFormat, out file);

        return TryResolveOverrideStem(relativeDirectory, clipName, out file);
    }

    internal static bool IsPreferredSoundEffectPath(string path, string logicalName, int oneBasedVariation)
    {
        if (!TryResolveSoundEffect(logicalName, oneBasedVariation, out ResolvedAudioFile preferred)) return false;
        return PathsEqual(path, preferred.Path);
    }

    internal static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { }
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveAssetStem(string relativeDirectory, string stem, out ResolvedAudioFile file)
    {
        file = default;
        ExternalAudioFormat[] snapshot = SnapshotFormats();
        for (int i = 0; i < snapshot.Length; i++)
        {
            ExternalAudioFormat format = snapshot[i];
            string relativePath = relativeDirectory + Path.DirectorySeparatorChar + stem + format.Extension;
            string resolved = AssetManager.ResolveFilePath(relativePath);
            if (!File.Exists(resolved)) continue;
            file = new ResolvedAudioFile(resolved, format);
            return true;
        }
        return false;
    }

    private static bool TryResolveOverrideStem(string relativeDirectory, string stem, out ResolvedAudioFile file)
    {
        file = default;
        ExternalAudioFormat[] snapshot = SnapshotFormats();
        for (int i = 0; i < snapshot.Length; i++)
        {
            ExternalAudioFormat format = snapshot[i];
            if (TryResolveOverrideExact(relativeDirectory, stem + format.Extension, format, out file)) return true;
        }
        return false;
    }

    private static bool TryResolveOverrideExact(
        string relativeDirectory,
        string fileName,
        ExternalAudioFormat format,
        out ResolvedAudioFile file)
    {
        file = default;
        string relativePath = relativeDirectory + Path.DirectorySeparatorChar + fileName;
        string resolved = AssetManager.ResolveFilePath(relativePath);
        if (!File.Exists(resolved)) return false;

        // Match vanilla LoadedSoundEffects semantics: only a path redirected by the asset/mod
        // resolver counts as a loose override; the bare game-root fallback still belongs to the
        // AssetBundle path.
        string vanillaFallback = Path.Combine(Custom.RootFolderDirectory(), relativePath.ToLowerInvariant());
        if (PathsEqual(resolved, vanillaFallback)) return false;

        file = new ResolvedAudioFile(resolved, format);
        return true;
    }

    private static ExternalAudioFormat[] SnapshotFormats()
    {
        lock (Gate) return Formats.ToArray();
    }

    private static void RegisterBuiltIn(ExternalAudioFormat format)
    {
        lock (Gate) AddFormatNoLock(format);
    }

    private static void AddFormatNoLock(ExternalAudioFormat format)
    {
        ByExtension.Add(format.Extension, format);
        Formats.Add(format);
        Formats.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
    }
}
