using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace DryCycle.Misc.SoundFormatSupport;

/// <summary>
/// Decodes a resolved loose-audio file into the AudioClip surface expected by Rain World.
/// Format selection stays in ExternalAudioFormatRegistry; decoder details stay here.
/// </summary>
internal static class ExternalAudioLoader
{
    private sealed class DecodedPcm
    {
        internal float[] Samples;
        internal int Channels;
        internal int SampleRate;
    }

    internal static IEnumerator LoadCoroutine(
        ResolvedAudioFile file,
        string clipName,
        Action<AudioClip> onLoaded,
        Action<string> onFailed)
    {
        if (file.Format.Decoder == ExternalAudioDecoderKind.MediaFoundation &&
            IsWindows() &&
            IsMediaFoundationBackendAvailable())
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

    private static bool IsMediaFoundationBackendAvailable() =>
        ResolveMediaFoundationReaderType() != null;

    private static Type ResolveMediaFoundationReaderType()
    {
        try
        {
            return Type.GetType(
                "NAudio.Wave.MediaFoundationReader, NAudio.Wasapi",
                throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    private static DecodedPcm DecodeWithMediaFoundation(string path)
    {
        Type readerType = ResolveMediaFoundationReaderType();
        if (readerType == null)
            throw new NotSupportedException(
                "Optional NAudio.Wasapi MediaFoundation backend is not installed.");

        object reader = null;
        try
        {
            reader = Activator.CreateInstance(readerType, path)
                ?? throw new InvalidOperationException("NAudio MediaFoundationReader construction returned null.");

            PropertyInfo waveFormatProperty = readerType.GetProperty("WaveFormat");
            object waveFormat = waveFormatProperty?.GetValue(reader)
                ?? throw new MissingMemberException(readerType.FullName, "WaveFormat");

            Type waveFormatType = waveFormat.GetType();
            int channels = ReadIntProperty(waveFormat, waveFormatType, "Channels");
            int sampleRate = ReadIntProperty(waveFormat, waveFormatType, "SampleRate");
            int bitsPerSample = ReadIntProperty(waveFormat, waveFormatType, "BitsPerSample");
            string encodingName = waveFormatType.GetProperty("Encoding")?.GetValue(waveFormat)?.ToString() ?? string.Empty;

            if (channels < 1 || channels > 8)
                throw new NotSupportedException(
                    "Decoded channel count " + channels + " is outside Unity's 1-8 channel AudioClip range.");
            if (sampleRate <= 0)
                throw new InvalidOperationException("Media Foundation returned an invalid sample rate.");
            if (!string.Equals(encodingName, "Pcm", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    "Media Foundation returned unsupported sample encoding '" + encodingName + "'.");
            if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
                throw new NotSupportedException(
                    "Media Foundation returned unsupported PCM depth " + bitsPerSample + " bits.");

            MethodInfo readMethod = readerType.GetMethod(
                "Read",
                new[] { typeof(byte[]), typeof(int), typeof(int) })
                ?? throw new MissingMethodException(readerType.FullName, "Read(byte[], int, int)");

            int bytesPerSample = bitsPerSample / 8;
            int blockAlign = Math.Max(bytesPerSample * channels, bytesPerSample);
            byte[] byteBuffer = new byte[Math.Max(16384, blockAlign * 4096)];
            List<float> samplesOut = new();

            while (true)
            {
                object readResult = readMethod.Invoke(reader, new object[] { byteBuffer, 0, byteBuffer.Length });
                int read = readResult is int count ? count : 0;
                if (read <= 0)
                    break;

                int completeBytes = read - (read % bytesPerSample);
                for (int offset = 0; offset < completeBytes; offset += bytesPerSample)
                    samplesOut.Add(PcmToFloat(byteBuffer, offset, bitsPerSample));
            }

            if (samplesOut.Count == 0)
                throw new InvalidOperationException(
                    "Media Foundation decoded zero PCM samples from " + path + ".");

            int completeSampleCount = samplesOut.Count - samplesOut.Count % channels;
            if (completeSampleCount <= 0)
                throw new InvalidOperationException(
                    "Decoded audio does not contain a complete sample frame.");
            if (completeSampleCount != samplesOut.Count)
                samplesOut.RemoveRange(completeSampleCount, samplesOut.Count - completeSampleCount);

            return new DecodedPcm
            {
                Samples = samplesOut.ToArray(),
                Channels = channels,
                SampleRate = sampleRate
            };
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException != null)
        {
            throw invocation.InnerException;
        }
        finally
        {
            if (reader is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static int ReadIntProperty(object instance, Type type, string name)
    {
        object value = type.GetProperty(name)?.GetValue(instance);
        return value is int number
            ? number
            : throw new MissingMemberException(type.FullName, name);
    }

    private static float PcmToFloat(byte[] buffer, int offset, int bitsPerSample)
    {
        switch (bitsPerSample)
        {
            case 8:
                return (buffer[offset] - 128) / 128f;
            case 16:
                short sample16 = (short)(buffer[offset] | buffer[offset + 1] << 8);
                return sample16 / 32768f;
            case 24:
                int sample24 =
                    buffer[offset] |
                    buffer[offset + 1] << 8 |
                    buffer[offset + 2] << 16;
                if ((sample24 & 0x00800000) != 0)
                    sample24 |= unchecked((int)0xFF000000);
                return sample24 / 8388608f;
            case 32:
                int sample32 =
                    buffer[offset] |
                    buffer[offset + 1] << 8 |
                    buffer[offset + 2] << 16 |
                    buffer[offset + 3] << 24;
                return sample32 / 2147483648f;
            default:
                throw new NotSupportedException("Unsupported PCM depth " + bitsPerSample + " bits.");
        }
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
