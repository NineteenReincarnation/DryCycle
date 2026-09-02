using System;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Extremely restrained desert-heat ambience. HeatWave no longer owns a burst/explosion
/// transient: sound is only a quiet broad-band pressure bed that supports the visual
/// dryness without becoming a weather event by itself.
/// </summary>
internal sealed class HeatWaveAudio : IDisposable
{
    private const int SampleRate = 22050;
    private const float WindSeconds = 8f;

    private static AudioClip _windClip;
    private static bool _clipGenerationAttempted;

    private readonly UpdatableAndDeletable _owner;
    private GameObject _audioObject;
    private AudioSource _windSource;
    private float _smoothedVolume;
    private bool _disposed;

    internal HeatWaveAudio(UpdatableAndDeletable owner)
    {
        _owner = owner;
    }

    internal void Update(
        float intensity,
        float solarIntensity,
        float visualTime)
    {
        if (_disposed)
        {
            return;
        }

        bool audibleRoom = IsCameraRoom();
        float sfxVolume = ResolveSfxVolume();

        // Heat itself should not sound like a storm. Keep the bed quiet enough that
        // players mainly notice its absence/presence subconsciously.
        float target = audibleRoom
            ? Mathf.Clamp01(intensity) *
              Mathf.Lerp(0.018f, 0.036f, Mathf.Clamp01(solarIntensity)) *
              sfxVolume
            : 0f;

        _smoothedVolume = Mathf.MoveTowards(
            _smoothedVolume,
            target,
            0.0025f);

        if (_smoothedVolume > 0.0004f && EnsureSource())
        {
            _windSource.volume = _smoothedVolume;
            _windSource.pitch = 0.985f +
                Mathf.Sin(visualTime * 0.11f) * 0.007f +
                Mathf.Sin(visualTime * 0.047f + 1.3f) * 0.004f;

            if (!_windSource.isPlaying)
            {
                _windSource.Play();
            }
        }
        else if (_windSource != null)
        {
            _windSource.volume = _smoothedVolume;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_audioObject != null)
        {
            UnityEngine.Object.Destroy(_audioObject);
            _audioObject = null;
            _windSource = null;
        }
    }

    private bool EnsureSource()
    {
        if (_windSource != null)
        {
            return true;
        }

        EnsureClip();
        if (_windClip == null)
        {
            return false;
        }

        try
        {
            _audioObject = new GameObject("DryCycle HeatWave Air")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            _windSource = _audioObject.AddComponent<AudioSource>();
            _windSource.playOnAwake = false;
            _windSource.loop = true;
            _windSource.spatialBlend = 0f;
            _windSource.dopplerLevel = 0f;
            _windSource.priority = 112;
            _windSource.clip = _windClip;
            _windSource.volume = 0f;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle HeatWave ambient audio disabled: {ex.Message}");
            Dispose();
            return false;
        }
    }

    private bool IsCameraRoom()
    {
        Room room = _owner?.room;
        RainWorldGame game = room?.game;
        return game?.cameras != null &&
               game.cameras.Length > 0 &&
               game.cameras[0]?.room == room;
    }

    private float ResolveSfxVolume()
    {
        try
        {
            return Mathf.Clamp01(
                _owner?.room?.game?.rainWorld?.options?.soundEffectsVolume ?? 0.8f);
        }
        catch
        {
            return 0.8f;
        }
    }

    private static void EnsureClip()
    {
        if (_clipGenerationAttempted)
        {
            return;
        }

        _clipGenerationAttempted = true;
        try
        {
            _windClip = BuildWindClip();
            Plugin.Logger?.LogInfo(
                "DryCycle HeatWave ambient air clip generated in memory.");
        }
        catch (Exception ex)
        {
            _windClip = null;
            Plugin.Logger?.LogWarning(
                $"DryCycle HeatWave ambient synthesis failed: {ex.Message}");
        }
    }

    private static AudioClip BuildWindClip()
    {
        int frames = Mathf.RoundToInt(SampleRate * WindSeconds);
        float[] mono = new float[frames];
        System.Random random = new(0x4A19B37);

        // Integer cycle counts guarantee a seamless loop. Most energy stays in the
        // low-mid air-pressure range; high-frequency sand-like hiss is intentionally
        // almost absent.
        AddOscillatorBand(mono, random, 14, 32f, 150f, 0.055f);
        AddOscillatorBand(mono, random, 12, 150f, 520f, 0.022f);
        AddOscillatorBand(mono, random, 6, 520f, 1250f, 0.0045f);

        for (int i = 0; i < frames; i++)
        {
            float phase = i / (float)frames;
            float breathing =
                0.78f +
                Mathf.Sin(phase * Mathf.PI * 4f + 0.2f) * 0.065f +
                Mathf.Sin(phase * Mathf.PI * 8f + 1.7f) * 0.030f;
            mono[i] = SoftClip(mono[i] * breathing);
        }

        Normalize(mono, 0.52f);
        float[] stereo = new float[frames * 2];
        int delayA = Mathf.RoundToInt(SampleRate * 0.037f);
        int delayB = Mathf.RoundToInt(SampleRate * 0.071f);
        for (int i = 0; i < frames; i++)
        {
            float left = mono[i];
            float right =
                mono[(i + delayA) % frames] * 0.82f +
                mono[(i + frames - delayB) % frames] * 0.18f;
            stereo[i * 2] = left;
            stereo[i * 2 + 1] = right;
        }

        AudioClip clip = AudioClip.Create(
            "DC_HeatWave_Air_Procedural",
            frames,
            2,
            SampleRate,
            stream: false);
        clip.SetData(stereo, 0);
        return clip;
    }

    private static void AddOscillatorBand(
        float[] target,
        System.Random random,
        int count,
        float minHz,
        float maxHz,
        float bandGain)
    {
        int frames = target.Length;
        for (int oscillator = 0; oscillator < count; oscillator++)
        {
            float randomHz = Mathf.Lerp(minHz, maxHz, (float)random.NextDouble());
            int cycles = Mathf.Max(1, Mathf.RoundToInt(randomHz * WindSeconds));
            float phase = (float)random.NextDouble() * Mathf.PI * 2f;
            float amplitude = bandGain * Mathf.Lerp(0.55f, 1f, (float)random.NextDouble());
            float phaseStep = Mathf.PI * 2f * cycles / frames;

            for (int i = 0; i < frames; i++)
            {
                target[i] += Mathf.Sin(phase + phaseStep * i) * amplitude;
            }
        }
    }

    private static float SoftClip(float value)
    {
        return value / (1f + Mathf.Abs(value));
    }

    private static void Normalize(float[] samples, float peak)
    {
        float max = 0.000001f;
        for (int i = 0; i < samples.Length; i++)
        {
            max = Mathf.Max(max, Mathf.Abs(samples[i]));
        }

        float scale = peak / max;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= scale;
        }
    }
}
