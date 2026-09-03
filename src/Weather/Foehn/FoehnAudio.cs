using System;
using UnityEngine;

namespace DryCycle.Weather.Foehn;

/// <summary>
/// Procedural Foehn gale bed. Unlike HeatWave's quiet ambience this is intentionally
/// obvious: low pressure, fast dry-air hiss and gust modulation reinforce the same
/// large directional pulses used by the optical and particle systems.
/// </summary>
internal sealed class FoehnAudio : IDisposable
{
    private const int SampleRate = 22050;
    private const float LoopSeconds = 8f;

    private static AudioClip _galeClip;
    private static bool _generationAttempted;

    private readonly UpdatableAndDeletable _owner;
    private GameObject _audioObject;
    private AudioSource _source;
    private float _smoothedVolume;
    private bool _disposed;

    internal FoehnAudio(UpdatableAndDeletable owner)
    {
        _owner = owner;
    }

    internal void Update(float intensity, float visualTime)
    {
        if (_disposed)
        {
            return;
        }

        float drive = Mathf.Clamp01(intensity);
        float gustA = Mathf.Sin(visualTime * 1.71f + 0.31f);
        float gustB = Mathf.Sin(visualTime * 3.83f + 1.77f);
        float gust = Mathf.Clamp01(0.66f + gustA * 0.22f + gustB * 0.12f);

        float target = IsCameraRoom()
            ? Mathf.Pow(drive, 0.72f) *
              Mathf.Lerp(0.095f, 0.205f, gust) *
              ResolveSfxVolume()
            : 0f;

        _smoothedVolume = Mathf.MoveTowards(
            _smoothedVolume,
            target,
            target > _smoothedVolume ? 0.0065f : 0.0040f);

        if (_smoothedVolume > 0.0004f && EnsureSource())
        {
            _source.volume = _smoothedVolume;
            _source.pitch =
                0.93f + drive * 0.10f + gust * 0.035f +
                Mathf.Sin(visualTime * 0.43f) * 0.008f;

            if (!_source.isPlaying)
            {
                _source.Play();
            }
        }
        else if (_source != null)
        {
            _source.volume = _smoothedVolume;
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
            _source = null;
        }
    }

    private bool EnsureSource()
    {
        if (_source != null)
        {
            return true;
        }

        EnsureClip();
        if (_galeClip == null)
        {
            return false;
        }

        try
        {
            _audioObject = new GameObject("DryCycle Foehn Gale")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            _source = _audioObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;
            _source.dopplerLevel = 0f;
            _source.priority = 104;
            _source.clip = _galeClip;
            _source.volume = 0f;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle Foehn gale audio disabled: {ex.Message}");
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
        if (_generationAttempted)
        {
            return;
        }

        _generationAttempted = true;
        try
        {
            _galeClip = BuildGaleClip();
            Plugin.Logger?.LogInfo(
                "DryCycle Foehn procedural gale clip generated in memory.");
        }
        catch (Exception ex)
        {
            _galeClip = null;
            Plugin.Logger?.LogWarning(
                $"DryCycle Foehn gale synthesis failed: {ex.Message}");
        }
    }

    private static AudioClip BuildGaleClip()
    {
        int frames = Mathf.RoundToInt(SampleRate * LoopSeconds);
        float[] mono = new float[frames];
        System.Random random = new(0x1F0E4A7);

        // Broad pressure body.
        AddOscillatorBand(mono, random, 18, 24f, 145f, 0.080f);
        AddOscillatorBand(mono, random, 22, 145f, 620f, 0.047f);

        // Dry-air hiss: many small high-frequency components rather than white noise,
        // so the loop remains seamless and does not sound like rain static.
        AddOscillatorBand(mono, random, 30, 620f, 2200f, 0.0105f);
        AddOscillatorBand(mono, random, 22, 2200f, 4800f, 0.0048f);

        for (int i = 0; i < frames; i++)
        {
            float phase = i / (float)frames;
            float macroGust =
                0.73f +
                Mathf.Sin(phase * Mathf.PI * 4f + 0.31f) * 0.14f +
                Mathf.Sin(phase * Mathf.PI * 10f + 1.91f) * 0.065f +
                Mathf.Sin(phase * Mathf.PI * 18f + 0.73f) * 0.028f;
            mono[i] = SoftClip(mono[i] * macroGust * 1.18f);
        }

        Normalize(mono, 0.72f);

        // Stereo decorrelation uses loop-safe delays. The moderate offset makes the
        // gale feel wide without pinning a fake source to either side of the screen.
        float[] stereo = new float[frames * 2];
        int delayA = Mathf.RoundToInt(SampleRate * 0.021f);
        int delayB = Mathf.RoundToInt(SampleRate * 0.057f);
        for (int i = 0; i < frames; i++)
        {
            float left =
                mono[i] * 0.88f +
                mono[(i + delayB) % frames] * 0.12f;
            float right =
                mono[(i + delayA) % frames] * 0.82f +
                mono[(i + frames - delayB) % frames] * 0.18f;
            stereo[i * 2] = left;
            stereo[i * 2 + 1] = right;
        }

        AudioClip clip = AudioClip.Create(
            "DC_Foehn_Gale_Procedural",
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
            int cycles = Mathf.Max(1, Mathf.RoundToInt(randomHz * LoopSeconds));
            float phase = (float)random.NextDouble() * Mathf.PI * 2f;
            float amplitude = bandGain * Mathf.Lerp(0.48f, 1f, (float)random.NextDouble());
            float phaseStep = Mathf.PI * 2f * cycles / frames;

            float sinValue = Mathf.Sin(phase);
            float cosValue = Mathf.Cos(phase);
            float sinStep = Mathf.Sin(phaseStep);
            float cosStep = Mathf.Cos(phaseStep);

            for (int i = 0; i < frames; i++)
            {
                target[i] += sinValue * amplitude;

                float nextSin = sinValue * cosStep + cosValue * sinStep;
                float nextCos = cosValue * cosStep - sinValue * sinStep;
                sinValue = nextSin;
                cosValue = nextCos;

                if ((i & 2047) == 2047)
                {
                    float invLength = 1f / Mathf.Max(
                        0.000001f,
                        Mathf.Sqrt(sinValue * sinValue + cosValue * cosValue));
                    sinValue *= invLength;
                    cosValue *= invLength;
                }
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
