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
/// New extensions should be registered here and should reuse an existing decoder backend whenever
/// possible; SoundLoader integration must not grow a second set of format-specific hooks.
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
        // Preserve Rain World's historical preference first: WAV, then OGG. Every additional
        // format gets a deterministic lower priority so duplicate files never race for one slot.
        RegisterBuiltIn(new ExternalAudioFormat(".wav", AudioType.WAV, ExternalAudioDecoderKind.Unity, true, 200));
        RegisterBuiltIn(new ExternalAudioFormat(".wave", AudioType.WAV, ExternalAudioDecoderKind.Unity, true, 199));
        RegisterBuiltIn(new ExternalAudioFormat(".ogg", AudioType.OGGVORBIS, ExternalAudioDecoderKind.Unity, true, 190));
        RegisterBuiltIn(new ExternalAudioFormat(".oga", AudioType.OGGVORBIS, ExternalAudioDecoderKind.Unity, true, 189));
        RegisterBuiltIn(new ExternalAudioFormat(".mp3", AudioType.MPEG, ExternalAudioDecoderKind.Unity, true, 180));
        RegisterBuiltIn(new ExternalAudioFormat(".mp2", AudioType.MPEG, ExternalAudioDecoderKind.Unity, true, 179));
        RegisterBuiltIn(new ExternalAudioFormat(".aiff", AudioType.AIFF, ExternalAudioDecoderKind.Unity, false, 170));
        RegisterBuiltIn(new ExternalAudioFormat(".aif", AudioType.AIFF, ExternalAudioDecoderKind.Unity, false, 169));

        // Rain World's Unity generation has no usable AudioType entry for these formats. Windows
        // therefore uses the same Media Foundation backend for all of them. This includes AAC in
        // ADTS/M4A containers, WMA, FLAC and Opus. Non-Windows builds make one UNKNOWN-type Unity
        // fallback attempt in ExternalAudioLoader instead of pretending a dedicated decoder exists.
        RegisterBuiltIn(new ExternalAudioFormat(".flac", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 160));
        RegisterBuiltIn(new ExternalAudioFormat(".m4a", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 150));
        RegisterBuiltIn(new ExternalAudioFormat(".aac", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 149));
        RegisterBuiltIn(new ExternalAudioFormat(".adts", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 148));
        RegisterBuiltIn(new ExternalAudioFormat(".wma", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 140));
        RegisterBuiltIn(new ExternalAudioFormat(".opus", AudioType.UNKNOWN, ExternalAudioDecoderKind.MediaFoundation, false, 130));

        // Unity/FM0D also exposes the classic tracker-module formats. They use the same Unity
        // loader as ordinary loose sound files and do not need any tracker-specific SoundLoader hook.
        RegisterBuiltIn(new ExternalAudioFormat(".mod", AudioType.MOD, ExternalAudioDecoderKind.Unity, false, 100));
        RegisterBuiltIn(new ExternalAudioFormat(".xm", AudioType.XM, ExternalAudioDecoderKind.Unity, false, 99));
        RegisterBuiltIn(new ExternalAudioFormat(".it", AudioType.IT, ExternalAudioDecoderKind.Unity, false, 98));
        RegisterBuiltIn(new ExternalAudioFormat(".s3m", AudioType.S3M, ExternalAudioDecoderKind.Unity, false, 97));
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

    /// <summary>
    /// Extension point for future formats that can reuse one of the existing decoder backends.
    /// Registration is deterministic and does not install any additional Rain World hooks.
    /// </summary>
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
            // A numbered family wins over the unnumbered spelling. This prevents Foo.wav and
            // Foo_1.flac (for example) from racing for allAudio[x].audio[0].
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

        // First preserve vanilla's exact-name override semantics. If no exact override exists,
        // resolve the same logical stem across all supported formats so a loose .flac/.m4a/etc.
        // can replace an AssetBundle or loose ambient clip authored under another extension.
        if (!string.IsNullOrEmpty(extension) && TryGetFormat(clipName, out ExternalAudioFormat exactFormat))
        {
            if (TryResolveOverrideExact(relativeDirectory, clipName, exactFormat, out file)) return true;
            string stem;
            try { stem = Path.GetFileNameWithoutExtension(clipName); }
            catch { stem = string.Empty; }
            if (!string.IsNullOrWhiteSpace(stem)) return TryResolveOverrideStem(relativeDirectory, stem, out file);
            return false;
        }

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
