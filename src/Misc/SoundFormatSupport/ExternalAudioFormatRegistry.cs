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
/// New formats are added here and reuse one of the decoder backends in ExternalAudioLoader;
/// SoundLoader integration itself must not grow format-specific hooks.
/// </summary>
internal static class ExternalAudioFormatRegistry
{
    private const int MaxVariations = 100;

    private static readonly object Gate = new();
    private static readonly List<ExternalAudioFormat> Formats = new();
    private static readonly Dictionary<string, ExternalAudioFormat> ByExtension =
        new(StringComparer.OrdinalIgnoreCase);
    // SoundClipReady runs on the gameplay hot path. A cache miss used to probe every supported
    // extension through AssetManager.ResolveFilePath + File.Exists on every sound playback (jump,
    // footsteps, impacts, etc.). Cache both positive and negative LoadedSoundEffects lookups.
    private static readonly Dictionary<string, ResolvedAudioFile?> LoadedSoundEffectResolutionCache =
        new(StringComparer.OrdinalIgnoreCase);

    static ExternalAudioFormatRegistry()
    {
        // Keep Rain World's historical loose-audio choices first, then deterministic aliases.
        RegisterUnity(".wav", AudioType.WAV, true, 300);
        RegisterUnity(".wave", AudioType.WAV, true, 299);
        RegisterUnity(".ogg", AudioType.OGGVORBIS, true, 290);
        RegisterUnity(".oga", AudioType.OGGVORBIS, true, 289);
        RegisterUnity(".mp3", AudioType.MPEG, true, 280);
        RegisterUnity(".mp2", AudioType.MPEG, true, 279);
        RegisterUnity(".mpa", AudioType.MPEG, true, 278);
        RegisterUnity(".aiff", AudioType.AIFF, false, 270);
        RegisterUnity(".aif", AudioType.AIFF, false, 269);

        // Classic tracker modules are native AudioType formats in the Unity generation used by
        // Rain World. They pass through exactly the same importer/variation path as WAV or MP3.
        RegisterUnity(".mod", AudioType.MOD, false, 250);
        RegisterUnity(".xm", AudioType.XM, false, 249);
        RegisterUnity(".it", AudioType.IT, false, 248);
        RegisterUnity(".s3m", AudioType.S3M, false, 247);

        // Media Foundation family. These all reuse one decoder backend; adding them does NOT add
        // any SoundLoader hooks. Availability of individual codecs ultimately follows the Windows
        // Media Foundation installation, while decode failure is isolated to the affected clip.
        RegisterMediaFoundation(".flac", 230);
        RegisterMediaFoundation(".m4a", 220);
        RegisterMediaFoundation(".aac", 219);
        RegisterMediaFoundation(".adts", 218);
        RegisterMediaFoundation(".wma", 210);
        RegisterMediaFoundation(".asf", 209);
        RegisterMediaFoundation(".opus", 200);

        // Audio-bearing MPEG-4 / 3GP containers. In SoundEffects directories these are treated as
        // audio assets; MediaFoundationReader extracts the audio stream and ignores video streams.
        RegisterMediaFoundation(".mp4", 190);
        RegisterMediaFoundation(".mov", 189);
        RegisterMediaFoundation(".3gp", 180);
        RegisterMediaFoundation(".3g2", 179);
        RegisterMediaFoundation(".3gp2", 178);
        RegisterMediaFoundation(".3gpp", 177);
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
    /// Extension point for future formats that can reuse Unity or Media Foundation decoding.
    /// Registration is deterministic and never installs a new Rain World hook.
    /// </summary>
    internal static bool RegisterFormat(ExternalAudioFormat format)
    {
        if (string.IsNullOrWhiteSpace(format.Extension)) return false;
        lock (Gate)
        {
            if (ByExtension.ContainsKey(format.Extension)) return false;
            AddFormatNoLock(format);
            LoadedSoundEffectResolutionCache.Clear();
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
            // A numbered family wins over the unnumbered spelling. This prevents two formats from
            // racing for allAudio[x].audio[0] when a mod accidentally ships both Foo and Foo_1.
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

        string stem = selectedStem.Trim();
        lock (Gate)
        {
            if (LoadedSoundEffectResolutionCache.TryGetValue(stem, out ResolvedAudioFile? cached))
            {
                if (!cached.HasValue) return false;
                file = cached.Value;
                return true;
            }
        }

        bool found = TryResolveOverrideStem("LoadedSoundEffects", stem, out ResolvedAudioFile resolved);
        lock (Gate)
            LoadedSoundEffectResolutionCache[stem] = found ? resolved : null;

        if (!found) return false;
        file = resolved;
        return true;
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
        {
            if (TryResolveOverrideExact(relativeDirectory, clipName, exactFormat, out file)) return true;

            string stem;
            try { stem = Path.GetFileNameWithoutExtension(clipName); }
            catch { stem = string.Empty; }
            if (!string.IsNullOrWhiteSpace(stem))
                return TryResolveOverrideStem(relativeDirectory, stem, out file);
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
            if (!TryResolveExistingFile(relativePath, out string resolved))
                continue;

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
        if (!TryResolveExistingFile(relativePath, out string resolved))
            return false;

        // Match vanilla LoadedSoundEffects semantics: only a path redirected by the asset/mod
        // resolver counts as a loose override; the bare game-root fallback still belongs to the
        // AssetBundle path.
        string vanillaFallback;
        try
        {
            vanillaFallback = Path.Combine(
                Custom.RootFolderDirectory(),
                relativePath.ToLowerInvariant());
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "ExternalAudioFormatRegistry/VanillaFallback/" + relativePath,
                error);
            return false;
        }

        if (PathsEqual(resolved, vanillaFallback)) return false;

        file = new ResolvedAudioFile(resolved, format);
        return true;
    }

    private static bool TryResolveExistingFile(string relativePath, out string resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        try
        {
            resolved = AssetManager.ResolveFilePath(relativePath);
            return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved);
        }
        catch (Exception error)
        {
            global::DryCycle.StartupDiagnostics.Failure(
                "ExternalAudioFormatRegistry/Resolve/" + relativePath,
                error);
            resolved = null;
            return false;
        }
    }

    private static ExternalAudioFormat[] SnapshotFormats()
    {
        lock (Gate) return Formats.ToArray();
    }

    private static void RegisterUnity(string extension, AudioType type, bool stream, int priority) =>
        RegisterBuiltIn(new ExternalAudioFormat(extension, type, ExternalAudioDecoderKind.Unity, stream, priority));

    private static void RegisterMediaFoundation(string extension, int priority) =>
        RegisterBuiltIn(new ExternalAudioFormat(
            extension,
            AudioType.UNKNOWN,
            ExternalAudioDecoderKind.MediaFoundation,
            false,
            priority));

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
