using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DryCycle.Misc.SoundFormatSupport;

/// <summary>
/// Immutable-at-runtime loose-audio index.
///
/// Disk and mod-overlay discovery happens only at explicit lifecycle boundaries (Enable / LoadSounds
/// reload). Gameplay lookups are dictionary-only and never call AssetManager.ResolveFilePath,
/// AssetManager.ListDirectory or File.Exists.
/// </summary>
internal static class ExternalAudioAssetIndex
{
    private const int MaxVariations = 100;
    private static readonly object Gate = new();

    private static Dictionary<string, ResolvedAudioFile> soundEffects =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ResolvedAudioFile> loadedSoundEffects =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ResolvedAudioFile> loadedAmbientByStem =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ResolvedAudioFile> loadedAmbientByFileName =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool ready;
    private static int generation;

    internal static int Generation
    {
        get
        {
            lock (Gate) return generation;
        }
    }

    internal static void Refresh(string reason)
    {
        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            ExternalAudioFormat[] formats = ExternalAudioFormatRegistry.SnapshotFormats();

            Dictionary<string, ResolvedAudioFile> nextSoundEffects =
                ScanDirectory("SoundEffects", moddedOnly: false, formats, includeFileNameIndex: false, out _);

            Dictionary<string, ResolvedAudioFile> nextLoadedSoundEffects =
                ScanDirectory("LoadedSoundEffects", moddedOnly: true, formats, includeFileNameIndex: false, out _);

            Dictionary<string, ResolvedAudioFile> nextLoadedAmbient =
                ScanDirectory(
                    Path.Combine("LoadedSoundEffects", "Ambient"),
                    moddedOnly: true,
                    formats,
                    includeFileNameIndex: true,
                    out Dictionary<string, ResolvedAudioFile> nextLoadedAmbientByFileName);

            lock (Gate)
            {
                soundEffects = nextSoundEffects;
                loadedSoundEffects = nextLoadedSoundEffects;
                loadedAmbientByStem = nextLoadedAmbient;
                loadedAmbientByFileName = nextLoadedAmbientByFileName;
                ready = true;
                generation++;
            }

            clock.Stop();
            global::DryCycle.StartupDiagnostics.Marker(
                "ExternalAudioAssetIndex.Refresh",
                "READY",
                "reason=" + (reason ?? "unspecified") +
                ", generation=" + Generation +
                ", SoundEffects=" + nextSoundEffects.Count +
                ", LoadedSoundEffects=" + nextLoadedSoundEffects.Count +
                ", Ambient=" + nextLoadedAmbient.Count +
                ", elapsedMs=" + clock.ElapsedMilliseconds);
        }
        catch (Exception error)
        {
            clock.Stop();
            global::DryCycle.StartupDiagnostics.Failure(
                "ExternalAudioAssetIndex.Refresh/" + (reason ?? "unspecified"),
                error);
            throw;
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            soundEffects = new Dictionary<string, ResolvedAudioFile>(StringComparer.OrdinalIgnoreCase);
            loadedSoundEffects = new Dictionary<string, ResolvedAudioFile>(StringComparer.OrdinalIgnoreCase);
            loadedAmbientByStem = new Dictionary<string, ResolvedAudioFile>(StringComparer.OrdinalIgnoreCase);
            loadedAmbientByFileName = new Dictionary<string, ResolvedAudioFile>(StringComparer.OrdinalIgnoreCase);
            ready = false;
            generation++;
        }
    }

    internal static bool TryResolveSoundEffect(
        string logicalName,
        int oneBasedVariation,
        out ResolvedAudioFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(logicalName) || oneBasedVariation < 1 || !IsReady())
            return false;

        string stem = logicalName.Trim();
        lock (Gate)
        {
            if (oneBasedVariation == 1)
            {
                if (soundEffects.TryGetValue(stem + "_1", out file)) return true;
                return soundEffects.TryGetValue(stem, out file);
            }

            return soundEffects.TryGetValue(stem + "_" + oneBasedVariation, out file);
        }
    }

    internal static int CountSoundEffectVariations(string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName) || !IsReady()) return 0;
        string stem = logicalName.Trim();

        lock (Gate)
        {
            if (!soundEffects.ContainsKey(stem + "_1") && !soundEffects.ContainsKey(stem))
                return 0;

            int count = 1;
            for (int variation = 2; variation <= MaxVariations; variation++)
            {
                if (!soundEffects.ContainsKey(stem + "_" + variation)) break;
                count++;
            }
            return count;
        }
    }

    internal static bool TryResolveLoadedSoundEffect(string selectedStem, out ResolvedAudioFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(selectedStem) || !IsReady()) return false;
        lock (Gate)
            return loadedSoundEffects.TryGetValue(selectedStem.Trim(), out file);
    }

    internal static bool TryResolveLoadedAmbient(string clipName, out ResolvedAudioFile file)
    {
        file = default;
        if (string.IsNullOrWhiteSpace(clipName) || !IsReady()) return false;

        string fileName;
        string extension;
        try
        {
            fileName = Path.GetFileName(clipName);
            extension = Path.GetExtension(fileName);
        }
        catch
        {
            return false;
        }

        lock (Gate)
        {
            if (!string.IsNullOrEmpty(extension) &&
                loadedAmbientByFileName.TryGetValue(fileName, out file))
                return true;

            string stem;
            try { stem = Path.GetFileNameWithoutExtension(fileName); }
            catch { return false; }

            if (string.IsNullOrWhiteSpace(stem)) stem = fileName;
            return loadedAmbientByStem.TryGetValue(stem, out file);
        }
    }

    internal static bool IsPreferredSoundEffectPath(
        string path,
        string logicalName,
        int oneBasedVariation)
    {
        return TryResolveSoundEffect(logicalName, oneBasedVariation, out ResolvedAudioFile preferred) &&
               ExternalAudioFormatRegistry.PathsEqual(path, preferred.Path);
    }

    private static bool IsReady()
    {
        lock (Gate) return ready;
    }

    private static Dictionary<string, ResolvedAudioFile> ScanDirectory(
        string relativeDirectory,
        bool moddedOnly,
        ExternalAudioFormat[] formats,
        bool includeFileNameIndex,
        out Dictionary<string, ResolvedAudioFile> byFileName)
    {
        Dictionary<string, ResolvedAudioFile> byStem =
            new(StringComparer.OrdinalIgnoreCase);
        byFileName = includeFileNameIndex
            ? new Dictionary<string, ResolvedAudioFile>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ResolvedAudioFile>(StringComparer.OrdinalIgnoreCase);

        // AssetManager.ListDirectory already walks mergedmods, active mods in override order,
        // console files and (when requested) the vanilla root while de-duplicating identical file
        // names. We preserve that ownership model rather than inventing a second mod resolver.
        string[] files = AssetManager.ListDirectory(
            relativeDirectory,
            directories: false,
            includeAll: false,
            moddedOnly: moddedOnly);

        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            if (!ExternalAudioFormatRegistry.TryGetFormat(path, out ExternalAudioFormat format))
                continue;

            string stem;
            string fileName;
            try
            {
                stem = Path.GetFileNameWithoutExtension(path);
                fileName = Path.GetFileName(path);
            }
            catch (Exception error)
            {
                global::DryCycle.StartupDiagnostics.Failure(
                    "ExternalAudioAssetIndex/Path/" + relativeDirectory,
                    error);
                continue;
            }

            if (string.IsNullOrWhiteSpace(stem)) continue;
            ResolvedAudioFile candidate = new(path, format);
            AddPreferred(byStem, stem, candidate);

            if (includeFileNameIndex && !string.IsNullOrWhiteSpace(fileName))
                AddPreferred(byFileName, fileName, candidate);
        }

        return byStem;
    }

    private static void AddPreferred(
        Dictionary<string, ResolvedAudioFile> target,
        string key,
        ResolvedAudioFile candidate)
    {
        if (!target.TryGetValue(key, out ResolvedAudioFile existing) ||
            candidate.Format.Priority > existing.Format.Priority)
        {
            target[key] = candidate;
        }
    }
}
