using System;
using System.Collections;
using System.IO;
using AssetBundles;
using RWCustom;
using UnityEngine;

namespace DryCycle.Misc.SoundFormatSupport;

/// <summary>
/// Extends Rain World's loose SoundLoader pipeline without replacing its public sound model.
/// OGG/WAV/MP3/M4A all pass through the same resolver so existence checks, variation counting,
/// import, ambient playback and LoadedSoundEffects overrides cannot disagree about formats.
/// </summary>
internal static class SoundFormatSupportRuntime
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;

        On.SoundLoader.SoundImporter.validFileType += SoundImporter_validFileType;
        On.SoundLoader.SoundImporter.loadFile += SoundImporter_loadFile;
        On.SoundLoader.AmbientImporter.validFileType += AmbientImporter_validFileType;
        On.SoundLoader.AmbientImporter.loadFile += AmbientImporter_loadFile;
        On.SoundLoader.CheckIfFileExistsAsExternal += SoundLoader_CheckIfFileExistsAsExternal;
        On.SoundLoader.VariationsForSound += SoundLoader_VariationsForSound;
        On.SoundLoader.RequestAmbientAudioClip += SoundLoader_RequestAmbientAudioClip;
        On.SoundLoader.LoadSounds += SoundLoader_LoadSounds;

        enabled = true;
        Plugin.Logger?.LogInfo("Sound format support enabled without RuntimeDetour: " + string.Join(", ", ExternalAudioFormatRegistry.SupportedExtensions));
    }

    internal static void Disable()
    {
        if (!enabled) return;

        RemoveOnHooks();
        enabled = false;
    }

    internal static void HydrateExisting(SoundLoader loader)
    {
        if (!enabled || loader == null) return;
        HydrateLoadedSoundEffectOverrides(loader);
    }

    private static void RemoveOnHooks()
    {
        On.SoundLoader.LoadSounds -= SoundLoader_LoadSounds;
        On.SoundLoader.RequestAmbientAudioClip -= SoundLoader_RequestAmbientAudioClip;
        On.SoundLoader.VariationsForSound -= SoundLoader_VariationsForSound;
        On.SoundLoader.CheckIfFileExistsAsExternal -= SoundLoader_CheckIfFileExistsAsExternal;
        On.SoundLoader.AmbientImporter.loadFile -= AmbientImporter_loadFile;
        On.SoundLoader.AmbientImporter.validFileType -= AmbientImporter_validFileType;
        On.SoundLoader.SoundImporter.loadFile -= SoundImporter_loadFile;
        On.SoundLoader.SoundImporter.validFileType -= SoundImporter_validFileType;
    }

    private static bool SoundImporter_validFileType(
        On.SoundLoader.SoundImporter.orig_validFileType orig,
        SoundLoader.SoundImporter self,
        string filename)
    {
        if (orig(self, filename)) return true;
        return ExternalAudioFormatRegistry.IsSupported(filename);
    }

    private static IEnumerator SoundImporter_loadFile(
        On.SoundLoader.SoundImporter.orig_loadFile orig,
        SoundLoader.SoundImporter self,
        string path,
        string name,
        IntVector2 index)
    {
        if (!ExternalAudioFormatRegistry.TryGetFormat(path, out _))
            return orig(self, path, name, index);

        if (!TryGetLogicalSound(self, index, out string logicalName, out int variation))
            return EmptyCoroutine();

        // reloadSounds enumerates files rather than logical samples. Filter duplicates here so one
        // variation results in exactly one coroutine even if several supported formats coexist.
        if (!ExternalAudioFormatRegistry.IsPreferredSoundEffectPath(path, logicalName, variation))
            return EmptyCoroutine();

        if (!ExternalAudioFormatRegistry.TryResolveSoundEffect(logicalName, variation, out ResolvedAudioFile resolved))
            return orig(self, path, name, index);

        return LoadSoundImporterClip(self, resolved, name, index);
    }

    private static bool AmbientImporter_validFileType(
        On.SoundLoader.AmbientImporter.orig_validFileType orig,
        SoundLoader.AmbientImporter self,
        string filename)
    {
        if (orig(self, filename)) return true;
        if (!ExternalAudioFormatRegistry.TryGetFormat(filename, out ExternalAudioFormat format)) return false;
        self.isWav = string.Equals(format.Extension, ".wav", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static IEnumerator AmbientImporter_loadFile(
        On.SoundLoader.AmbientImporter.orig_loadFile orig,
        SoundLoader.AmbientImporter self,
        SoundLoader.AmbientImporter importer,
        string path,
        string name)
    {
        if (!ExternalAudioFormatRegistry.TryGetFormat(path, out ExternalAudioFormat format))
            return orig(self, importer, path, name);

        return LoadAmbientImporterClip(importer, new ResolvedAudioFile(path, format), name);
    }

    private static bool SoundLoader_CheckIfFileExistsAsExternal(
        On.SoundLoader.orig_CheckIfFileExistsAsExternal orig,
        SoundLoader self,
        string name)
    {
        if (ExternalAudioFormatRegistry.TryResolveSoundEffect(name, 1, out _)) return true;
        return orig(self, name);
    }

    private static int SoundLoader_VariationsForSound(
        On.SoundLoader.orig_VariationsForSound orig,
        SoundLoader self,
        string name)
    {
        // AssetBundle sounds retain vanilla variation rules. Only the loose-file branch is replaced.
        if (self.CheckIfFileExistsAsUnityResource(name)) return orig(self, name);
        int count = ExternalAudioFormatRegistry.CountSoundEffectVariations(name);
        return count > 0 ? count : orig(self, name);
    }

    private static void SoundLoader_LoadSounds(
        On.SoundLoader.orig_LoadSounds orig,
        SoundLoader self)
    {
        orig(self);
        HydrateLoadedSoundEffectOverrides(self);
    }

    private static void HydrateLoadedSoundEffectOverrides(SoundLoader self)
    {
        if (self?.allAudio == null) return;

        for (int i = 0; i < self.allAudio.Length; i++)
        {
            SoundLoader.ClipLoadData data = self.allAudio[i];
            if (!data.audioClipThroughUnity || data.audio == null || data.audio.Length == 0)
                continue;

            int variationCount = Math.Min(data.soundVariations, data.audio.Length);
            for (int variationIndex = 0; variationIndex < variationCount; variationIndex++)
            {
                string logicalName = data.soundVariations > 1
                    ? data.name + "_" + (variationIndex + 1)
                    : data.name;

                if (!ExternalAudioFormatRegistry.TryResolveLoadedSoundEffect(
                        logicalName,
                        out ResolvedAudioFile file) ||
                    IsVanillaLoadedOverrideFormat(file.Format))
                    continue;

                AudioClip existing = data.audio[variationIndex];
                if (existing != null &&
                    string.Equals(existing.name, logicalName, StringComparison.OrdinalIgnoreCase))
                    continue;

                AudioClip clip = ExternalAudioLoader.LoadBlocking(
                    file,
                    logicalName,
                    stream: true,
                    out string error);
                if (clip == null)
                {
                    LogDecodeFailure("LoadedSoundEffects", file.Path, error, self.errors);
                    continue;
                }

                self.allAudio[i].audio[variationIndex] = clip;
                if (self.unityAudioLoaders != null &&
                    i < self.unityAudioLoaders.Length &&
                    self.unityAudioLoaders[i] != null &&
                    variationIndex < self.unityAudioLoaders[i].Length)
                {
                    // LoadSounds may already have queued the AssetBundle version for cached sounds.
                    // Drop our reference to that request so SoundLoader.Update cannot overwrite the
                    // higher-priority loose custom-format clip after it finishes.
                    self.unityAudioLoaders[i][variationIndex] = null;
                }
            }
        }
    }

    private static AudioClip SoundLoader_RequestAmbientAudioClip(
        On.SoundLoader.orig_RequestAmbientAudioClip orig,
        SoundLoader self,
        string clipName)
    {
        if (self == null || string.IsNullOrWhiteSpace(clipName)) return orig(self, clipName);
        if (!ExternalAudioFormatRegistry.TryResolveLoadedAmbient(clipName, out ResolvedAudioFile file)
            || IsVanillaLoadedOverrideFormat(file.Format))
            return orig(self, clipName);

        for (int i = 0; i < self.ambientClipsThroughUnity.Count; i++)
        {
            AudioClip cached = self.ambientClipsThroughUnity[i];
            if (cached != null && string.Equals(cached.name, clipName, StringComparison.Ordinal)) return cached;
        }

        AudioClip clip = ExternalAudioLoader.LoadBlocking(file, clipName, stream: true, out string error);
        if (clip == null)
        {
            // Vanilla treats every non-WAV loose ambient override as OGG. Falling through after an
            // MP3/M4A decode failure would therefore retry the same file with the wrong decoder.
            LogDecodeFailure("LoadedSoundEffects/Ambient", file.Path, error, self.errors);
            return null;
        }

        self.ambientClipsThroughUnity.Add(clip);
        return clip;
    }

    private static IEnumerator LoadSoundImporterClip(
        SoundLoader.SoundImporter importer,
        ResolvedAudioFile file,
        string name,
        IntVector2 index)
    {
        AudioClip loaded = null;
        string failure = null;
        IEnumerator load = ExternalAudioLoader.LoadCoroutine(
            file,
            name,
            clip => loaded = clip,
            error => failure = error);
        while (load.MoveNext()) yield return load.Current;

        if (loaded != null)
        {
            importer.loadingClips.Add(new SoundLoader.SoundImporter.ClipAndIndex(index, loaded));
            yield break;
        }

        SoundLoader owner = importer.owner;
        LogDecodeFailure("SoundEffects", file.Path, failure, owner?.errors);

        // SoundImporter.Init starts coroutines before LoadSounds assigns clipsToBeLoaded. Adding a
        // silent loaded clip instead of decrementing the counter here makes failure accounting race-
        // free: vanilla Update consumes it through the normal loadingClips path and decrements once.
        AudioClip silent = AudioClip.Create(name + "_DryCycleDecodeFailure", 1, 1, 44100, stream: false);
        silent.SetData(new[] { 0f }, 0);
        importer.loadingClips.Add(new SoundLoader.SoundImporter.ClipAndIndex(index, silent));
    }

    private static IEnumerator LoadAmbientImporterClip(
        SoundLoader.AmbientImporter importer,
        ResolvedAudioFile file,
        string name)
    {
        AudioClip loaded = null;
        string failure = null;
        IEnumerator load = ExternalAudioLoader.LoadCoroutine(
            file,
            name,
            clip => loaded = clip,
            error => failure = error);
        while (load.MoveNext()) yield return load.Current;

        if (loaded != null)
        {
            importer.loadedClip = loaded;
            yield break;
        }

        LogDecodeFailure("SoundEffects/Ambient", file.Path, failure, null);
    }

    private static bool TryGetLogicalSound(
        SoundLoader.SoundImporter importer,
        IntVector2 index,
        out string logicalName,
        out int variation)
    {
        logicalName = string.Empty;
        variation = index.y + 1;
        SoundLoader owner = importer?.owner;
        if (owner?.allAudio == null || index.x < 0 || index.x >= owner.allAudio.Length || variation < 1)
            return false;
        SoundLoader.ClipLoadData data = owner.allAudio[index.x];
        if (data.audio == null || index.y < 0 || index.y >= data.audio.Length) return false;
        logicalName = data.name;
        return !string.IsNullOrWhiteSpace(logicalName);
    }

    private static bool IsVanillaLoadedOverrideFormat(ExternalAudioFormat format) =>
        string.Equals(format.Extension, ".wav", StringComparison.OrdinalIgnoreCase)
        || string.Equals(format.Extension, ".ogg", StringComparison.OrdinalIgnoreCase);

    private static IEnumerator EmptyCoroutine()
    {
        yield break;
    }

    private static void LogDecodeFailure(string scope, string path, string error, System.Collections.Generic.List<string> errors)
    {
        string message = "DryCycle audio decode failed [" + scope + "]: " + path;
        if (!string.IsNullOrWhiteSpace(error)) message += " | " + error;
        Plugin.Logger?.LogWarning(message);
        errors?.Add(message);
    }
}
