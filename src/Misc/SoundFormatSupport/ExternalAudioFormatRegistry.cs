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
        }

        // Registration is a lifecycle operation, never a playback operation. Rebuild immediately so
        // no later gameplay lookup has to discover files or invalidate caches on demand.
        ExternalAudioAssetIndex.Refresh("format registration " + format.Extension);
        return true;
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

    internal static bool TryResolveSoundEffect(string logicalName, int oneBasedVariation, out ResolvedAudioFile file) =>
        ExternalAudioAssetIndex.TryResolveSoundEffect(logicalName, oneBasedVariation, out file);

    internal static int CountSoundEffectVariations(string logicalName) =>
        ExternalAudioAssetIndex.CountSoundEffectVariations(logicalName);

    internal static bool TryResolveLoadedSoundEffect(string selectedStem, out ResolvedAudioFile file) =>
        ExternalAudioAssetIndex.TryResolveLoadedSoundEffect(selectedStem, out file);

    internal static bool TryResolveLoadedAmbient(string clipName, out ResolvedAudioFile file) =>
        ExternalAudioAssetIndex.TryResolveLoadedAmbient(clipName, out file);

    internal static bool IsPreferredSoundEffectPath(string path, string logicalName, int oneBasedVariation) =>
        ExternalAudioAssetIndex.IsPreferredSoundEffectPath(path, logicalName, oneBasedVariation);

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

    internal static ExternalAudioFormat[] SnapshotFormats()
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
