using System;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Self-generated HeatWave sound bed. No Rain World/Watcher wind loop is borrowed:
/// the clips are synthesized deterministically in memory the first time HeatWave is
/// heard. Wind is deliberately low and air-like (no sand hiss); the Burst transient is
/// a pressure release with a soft attack, never an explosion impact.
/// </summary>
internal sealed class HeatWaveAudio : IDisposable
{
    private const int SampleRate = 22050;
    private const float WindSeconds = 8f;
    private const float BurstSeconds = 2.8f;

    private static AudioClip _windClip;
    private static AudioClip _burstClip;
    private static bool _clipGenerationAttempted;

    private readonly UpdatableAndDeletable _owner;
    private GameObject _audioObject;
    private AudioSource _windSource;
    private AudioSource _burstSource;
    private float _lastBurstKick;
    private float _smoothedVolume;
    private bool _disposed;

    internal HeatWaveAudio(UpdatableAndDeletable owner)
    {
        _owner = owner;
    }

    internal void Update(
        float intensity,
        float solarIntensity,
        float stillness,
        float burstStrength,
        float burstKick,
        float visualTime)
    {
        if (_disposed)
        {
            return;
        }

        bool audibleRoom = IsCameraRoom();
        float sfxVolume = ResolveSfxVolume();
        float target = audibleRoom
            ? Mathf.Clamp01(intensity) *
              Mathf.Lerp(0.055f, 0.095f, Mathf.Clamp01(solarIntensity)) *
              Mathf.Lerp(1f, 0.11f, Mathf.Clamp01(stillness)) *
              (1f + Mathf.Clamp01(burstStrength) * 0.28f) *
              sfxVolume
            : 0f;

        _smoothedVolume = Mathf.MoveTowards(
            _smoothedVolume,
            target,
            0.0075f);

        if ((_smoothedVolume > 0.0005f || (audibleRoom && burstKick > 0.025f)) &&
            EnsureSources())
        {
            _windSource.volume = _smoothedVolume;
            _windSource.pitch = 0.965f +
                Mathf.Sin(visualTime * 0.17f) * 0.014f +
                Mathf.Sin(visualTime * 0.071f + 1.7f) * 0.009f;

            if (!_windSource.isPlaying && _smoothedVolume > 0.0005f)
            {
                _windSource.Play();
            }

            if (audibleRoom &&
                _lastBurstKick <= 0.055f &&
                burstKick > 0.055f &&
                _burstClip != null)
            {
                float burstVolume =
                    (0.12f + Mathf.Clamp01(intensity) * 0.14f) *
                    sfxVolume;
                _burstSource.pitch = Mathf.Lerp(0.94f, 1.02f, Mathf.Clamp01(solarIntensity));
                _burstSource.PlayOneShot(_burstClip, burstVolume);
            }
        }

        _lastBurstKick = burstKick;
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
            _burstSource = null;
        }
    }

    private bool EnsureSources()
    {
        if (_windSource != null && _burstSource != null)
        {
            return true;
        }

        EnsureClips();
        if (_windClip == null || _burstClip == null)
        {
            return false;
        }

        try
        {
            _audioObject = new GameObject("DryCycle HeatWave Audio")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            _windSource = _audioObject.AddComponent<AudioSource>();
            _windSource.playOnAwake = false;
            _windSource.loop = true;
            _windSource.spatialBlend = 0f;
            _windSource.dopplerLevel = 0f;
            _windSource.priority = 96;
            _windSource.clip = _windClip;
            _windSource.volume = 0f;

            _burstSource = _audioObject.AddComponent<AudioSource>();
            _burstSource.playOnAwake = false;
            _burstSource.loop = false;
            _burstSource.spatialBlend = 0f;
            _burstSource.dopplerLevel = 0f;
            _burstSource.priority = 88;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle HeatWave procedural audio disabled: {ex.Message}");
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

    private static void EnsureClips()
    {
        if (_clipGenerationAttempted)
        {
            return;
        }

        _clipGenerationAttempted = true;
        try
        {
            _windClip = BuildWindClip();
            _burstClip = BuildBurstClip();
            Plugin.Logger?.LogInfo(
                "DryCycle HeatWave procedural audio generated in memory.");
        }
        catch (Exception ex)
        {
            _windClip = null;
            _burstClip = null;
            Plugin.Logger?.LogWarning(
                $"DryCycle HeatWave audio synthesis failed: {ex.Message}");
        }
    }

    private static AudioClip BuildWindClip()
    {
        int frames = Mathf.RoundToInt(SampleRate * WindSeconds);
        float[] mono = new float[frames];
        System.Random random = new(0x4A19B37);

        // Frequencies are integer cycle counts over the loop duration, so every
        // oscillator joins exactly at the loop boundary. The spectrum strongly favors
        // low/mid air pressure and intentionally avoids bright sandy hiss.
        AddOscillatorBand(mono, random, 18, 38f, 180f, 0.080f);
        AddOscillatorBand(mono, random, 18, 180f, 680f, 0.040f);
        AddOscillatorBand(mono, random, 12, 680f, 2100f, 0.012f);

        for (int i = 0; i < frames; i++)
        {
            float phase = i / (float)frames;
            float breathing =
                0.72f +
                Mathf.Sin(phase * Mathf.PI * 4f + 0.2f) * 0.10f +
                Mathf.Sin(phase * Mathf.PI * 10f + 1.7f) * 0.055f +
                Mathf.Sin(phase * Mathf.PI * 18f + 0.9f) * 0.028f;
            mono[i] = SoftClip(mono[i] * breathing * 1.18f);
        }

        Normalize(mono, 0.72f);
        float[] stereo = new float[frames * 2];
        int delayA = Mathf.RoundToInt(SampleRate * 0.031f);
        int delayB = Mathf.RoundToInt(SampleRate * 0.083f);
        for (int i = 0; i < frames; i++)
        {
            float left = mono[i];
            float right =
                mono[(i + delayA) % frames] * 0.84f +
                mono[(i + frames - delayB) % frames] * 0.16f;
            stereo[i * 2] = left;
            stereo[i * 2 + 1] = right;
        }

        AudioClip clip = AudioClip.Create(
            "DC_HeatWave_Wind_Procedural",
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

    private static AudioClip BuildBurstClip()
    {
        int frames = Mathf.RoundToInt(SampleRate * BurstSeconds);
        float[] mono = new float[frames];
        uint randomState = 0xC6A4A793u;
        float lowA = 0f;
        float lowB = 0f;
        float phase = 0f;

        for (int i = 0; i < frames; i++)
        {
            float time = i / (float)SampleRate;
            randomState = randomState * 1664525u + 1013904223u;
            float noise = ((randomState >> 8) & 0x00FFFFFFu) / 8388607.5f - 1f;

            lowA += (noise - lowA) * 0.085f;
            lowB += (lowA - lowB) * 0.031f;
            float air = lowA - lowB * 0.62f;

            float frequency = 68f + time * 28f;
            phase += Mathf.PI * 2f * frequency / SampleRate;
            float pressure =
                Mathf.Sin(phase) * 0.15f +
                Mathf.Sin(phase * 1.57f + 0.8f) * 0.065f;

            float attack = 1f - Mathf.Exp(-time / 0.052f);
            float release = Mathf.Exp(-Mathf.Max(0f, time - 0.14f) / 0.88f);
            float envelope = attack * release;
            mono[i] = SoftClip((air * 1.35f + pressure) * envelope * 1.32f);
        }

        Normalize(mono, 0.78f);
        float[] stereo = new float[frames * 2];
        int delay = Mathf.RoundToInt(SampleRate * 0.012f);
        for (int i = 0; i < frames; i++)
        {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] =
                mono[(i + delay) % frames] * 0.90f +
                mono[i] * 0.10f;
        }

        AudioClip clip = AudioClip.Create(
            "DC_HeatWave_Burst_Procedural",
            frames,
            2,
            SampleRate,
            stream: false);
        clip.SetData(stereo, 0);
        return clip;
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
