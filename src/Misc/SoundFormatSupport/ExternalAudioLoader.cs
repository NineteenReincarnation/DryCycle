using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.Wave;
using UnityEngine;

namespace DryCycle.Misc.SoundFormatSupport;

/// <summary>
/// Decodes a resolved loose-audio file into the AudioClip surface expected by Rain World.
/// Format selection stays in ExternalAudioFormatRegistry; decoder details stay here.
/// </summary>
internal enum DeferredAudioLoadState
{
    Unsupported = 0,
    Pending = 1,
    Ready = 2,
    Failed = 3
}

internal static class ExternalAudioLoader
{
    private sealed class DecodedPcm
    {
        internal float[] Samples;
        internal int Channels;
        internal int SampleRate;
    }

    private sealed class DeferredDecode
    {
        internal Task<DecodedPcm> Task;
        internal AudioClip Clip;
        internal string Error;
        internal bool Failed;
    }

    private static readonly object DeferredGate = new();
    private static readonly Dictionary<string, DeferredDecode> DeferredDecodes =
        new(StringComparer.OrdinalIgnoreCase);

    internal static IEnumerator LoadCoroutine(
        ResolvedAudioFile file,
        string clipName,
        Action<AudioClip> onLoaded,
        Action<string> onFailed)
    {
        if (file.Format.Decoder == ExternalAudioDecoderKind.MediaFoundation && IsWindows())
        {
            Task<DecodedPcm> task = null;
            string taskStartError = null;
            try
            {
                task = Task.Run(() => DecodeWithMediaFoundation(file.Path));
            }
            catch (Exception error)
            {
                taskStartError = error.Message;
            }

            if (task == null)
            {
                onFailed?.Invoke(taskStartError ?? "Unable to start Media Foundation decode task.");
                yield break;
            }

            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted || task.IsCanceled)
            {
                string message = task.Exception?.GetBaseException().Message ?? "Media Foundation decode task was cancelled.";
                onFailed?.Invoke(message);
                yield break;
            }

            AudioClip clip = CreateClip(clipName, task.Result, out string createError);
            if (clip == null)
            {
                onFailed?.Invoke(createError);
                yield break;
            }
            onLoaded?.Invoke(clip);
            yield break;
        }

        IEnumerator unityLoad = LoadWithUnityCoroutine(file, clipName, onLoaded, onFailed);
        while (unityLoad.MoveNext()) yield return unityLoad.Current;
    }

    internal static DeferredAudioLoadState TryLoadDeferredMediaFoundation(
        ResolvedAudioFile file,
        string clipName,
        out AudioClip clip,
        out string error)
    {
        clip = null;
        error = null;
        if (file.Format.Decoder != ExternalAudioDecoderKind.MediaFoundation || !IsWindows())
            return DeferredAudioLoadState.Unsupported;

        DeferredDecode entry;
        lock (DeferredGate)
        {
            if (!DeferredDecodes.TryGetValue(file.Path, out entry))
            {
                entry = new DeferredDecode();
                try
                {
                    entry.Task = Task.Run(() => DecodeWithMediaFoundation(file.Path));
                }
                catch (Exception startError)
                {
                    entry.Failed = true;
                    entry.Error = startError.Message;
                }
                DeferredDecodes[file.Path] = entry;
            }

            if (entry.Clip != null)
            {
                clip = entry.Clip;
                return DeferredAudioLoadState.Ready;
            }
            if (entry.Failed)
            {
                error = entry.Error ?? "Deferred Media Foundation decode failed.";
                return DeferredAudioLoadState.Failed;
            }
            if (entry.Task == null)
            {
                entry.Failed = true;
                entry.Error = "Deferred Media Foundation decode task was not created.";
                error = entry.Error;
                return DeferredAudioLoadState.Failed;
            }
            if (!entry.Task.IsCompleted)
                return DeferredAudioLoadState.Pending;
            if (entry.Task.IsCanceled || entry.Task.IsFaulted)
            {
                entry.Failed = true;
                entry.Error = entry.Task.Exception?.GetBaseException().Message ??
                              "Deferred Media Foundation decode task was cancelled.";
                error = entry.Error;
                return DeferredAudioLoadState.Failed;
            }
        }

        // Unity AudioClip creation and SetData stay on the main thread. The expensive container /
        // codec decode already completed on the worker, so this finalization is bounded and contains
        // no file discovery or Media Foundation read loop.
        AudioClip created = CreateClip(clipName, entry.Task.Result, out string createError);
        lock (DeferredGate)
        {
            if (created == null)
            {
                entry.Failed = true;
                entry.Error = createError;
                error = createError;
                return DeferredAudioLoadState.Failed;
            }

            entry.Clip = created;
            clip = created;
            return DeferredAudioLoadState.Ready;
        }
    }

    internal static void ResetDeferredLoads()
    {
        lock (DeferredGate)
            DeferredDecodes.Clear();
    }

    internal static AudioClip LoadBlocking(ResolvedAudioFile file, string clipName, bool stream, out string error)
    {
        error = null;
        try
        {
            if (file.Format.Decoder == ExternalAudioDecoderKind.MediaFoundation && IsWindows())
            {
                DecodedPcm decoded = DecodeWithMediaFoundation(file.Path);
                return CreateClip(clipName, decoded, out error);
            }

            AudioClip clip = AssetManager.SafeWWWAudioClip(
                "file://" + file.Path,
                threeD: false,
                stream: stream && file.Format.SupportsUnityStreaming,
                file.Format.UnityAudioType);
            if (clip == null)
            {
                error = "Unity returned a null AudioClip.";
                return null;
            }
            clip.name = clipName;
            return clip;
        }
        catch (Exception exception)
        {
            // Formats assigned to Media Foundation have no dependable decoder in Rain World's old
            // Unity AudioType table. Non-Windows platforms still get one UNKNOWN-type sniffing pass
            // so a platform-native decoder can succeed without another SoundLoader integration path.
            if (file.Format.Decoder == ExternalAudioDecoderKind.MediaFoundation)
            {
                try
                {
                    AudioClip fallback = AssetManager.SafeWWWAudioClip(
                        "file://" + file.Path,
                        threeD: false,
                        stream: false,
                        AudioType.UNKNOWN);
                    if (fallback != null)
                    {
                        fallback.name = clipName;
                        return fallback;
                    }
                }
                catch { }
            }

            error = exception.Message;
            return null;
        }
    }

    private static IEnumerator LoadWithUnityCoroutine(
        ResolvedAudioFile file,
        string clipName,
        Action<AudioClip> onLoaded,
        Action<string> onFailed)
    {
        // Rain World itself still ships and uses the legacy WWW audio module. Keeping this coroutine
        // on the game's native compatibility path avoids adding UnityWebRequestAudioModule as a new
        // hard dependency solely to replace an API that is already present in the target runtime.
        // The suppression is intentionally scoped to this method so new obsolete API use still warns.
#pragma warning disable CS0618
        WWW www = null;
        AudioClip clip = null;
        string startError = null;
        try
        {
            www = new WWW("file://" + file.Path);
            clip = www.GetAudioClip(false, false, file.Format.UnityAudioType);
        }
        catch (Exception error)
        {
            startError = error.Message;
        }

        if (startError != null)
        {
            onFailed?.Invoke(startError);
            www?.Dispose();
            yield break;
        }

        while (clip != null && clip.loadState != AudioDataLoadState.Loaded && clip.loadState != AudioDataLoadState.Failed)
            yield return www;

        if (clip == null || clip.loadState == AudioDataLoadState.Failed)
        {
            string message = www?.error;
            onFailed?.Invoke(string.IsNullOrWhiteSpace(message) ? "Unity failed to decode the AudioClip." : message);
            www?.Dispose();
            yield break;
        }

        clip.name = clipName;
        onLoaded?.Invoke(clip);
        www?.Dispose();
#pragma warning restore CS0618
    }

    private static DecodedPcm DecodeWithMediaFoundation(string path)
    {
        using MediaFoundationReader reader = new(path);
        ISampleProvider provider = reader.ToSampleProvider();
        int channels = provider.WaveFormat.Channels;
        int sampleRate = provider.WaveFormat.SampleRate;
        if (channels < 1 || channels > 8)
            throw new NotSupportedException("Decoded channel count " + channels + " is outside Unity's 1-8 channel AudioClip range.");
        if (sampleRate <= 0)
            throw new InvalidOperationException("Media Foundation returned an invalid sample rate.");

        int estimated = 0;
        try
        {
            double samples = reader.TotalTime.TotalSeconds * sampleRate * channels;
            if (samples > 0d && samples < int.MaxValue) estimated = (int)samples;
        }
        catch { }

        List<float> samplesOut = estimated > 0 ? new List<float>(estimated) : new List<float>();
        float[] buffer = new float[Math.Max(4096, channels * 4096)];
        while (true)
        {
            int read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (int i = 0; i < read; i++) samplesOut.Add(buffer[i]);
        }

        if (samplesOut.Count == 0)
            throw new InvalidOperationException("Media Foundation decoded zero PCM samples from " + path + ".");

        int completeSampleCount = samplesOut.Count - samplesOut.Count % channels;
        if (completeSampleCount <= 0)
            throw new InvalidOperationException("Decoded audio does not contain a complete sample frame.");
        if (completeSampleCount != samplesOut.Count)
            samplesOut.RemoveRange(completeSampleCount, samplesOut.Count - completeSampleCount);

        return new DecodedPcm
        {
            Samples = samplesOut.ToArray(),
            Channels = channels,
            SampleRate = sampleRate
        };
    }

    private static AudioClip CreateClip(string clipName, DecodedPcm decoded, out string error)
    {
        error = null;
        if (decoded?.Samples == null || decoded.Samples.Length == 0 || decoded.Channels <= 0 || decoded.SampleRate <= 0)
        {
            error = "Decoded audio data is empty or invalid.";
            return null;
        }

        int frames = decoded.Samples.Length / decoded.Channels;
        if (frames <= 0)
        {
            error = "Decoded audio data contains no complete frames.";
            return null;
        }

        try
        {
            AudioClip clip = AudioClip.Create(clipName, frames, decoded.Channels, decoded.SampleRate, stream: false);
            if (clip == null || !clip.SetData(decoded.Samples, 0))
            {
                if (clip != null) UnityEngine.Object.Destroy(clip);
                error = "Unity rejected decoded PCM data.";
                return null;
            }
            return clip;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private static bool IsWindows() => Environment.OSVersion.Platform == PlatformID.Win32NT;
}
