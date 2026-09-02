using System;
using System.Collections.Generic;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Persistent obstacle-aware GPU heat/air simulation. Velocity is stored in normalized
/// room UV per second; thermal state R is temperature excess and G is retained boundary
/// energy. The optical field stores a temperature-gradient-derived refraction vector in
/// RG, temperature in B and near-source boundary-layer strength in A.
///
/// Simulation state has real temporal memory. A room first entered while HeatWave is
/// already active is primed from exposed terrain/HeatColumns so the atmosphere does not
/// visibly cold-start, while later frames are purely advected/evolved from history.
/// </summary>
internal sealed class HeatWaveThermalSimulation : IDisposable
{
    private const int CellsPerTile = 4;
    private const int MaxWidth = 1024;
    private const int MaxHeight = 512;
    private const int PressureIterations = 24;
    private const int MaxEmitters = 24;

    private readonly Room _room;
    private readonly HeatWaveTerrainField _terrain;
    private readonly List<HeatColumnEmitterSample> _emitters = new();
    private readonly Vector4[] _emitterStartRadius = new Vector4[MaxEmitters];
    private readonly Vector4[] _emitterEndStrength = new Vector4[MaxEmitters];
    private readonly Vector4[] _emitterFlow = new Vector4[MaxEmitters];
    private readonly Vector4[] _emitterShape = new Vector4[MaxEmitters];

    private ComputeShader _solver;
    private RenderTexture _velocityRead;
    private RenderTexture _velocityWrite;
    private RenderTexture _thermalRead;
    private RenderTexture _thermalWrite;
    private RenderTexture _pressureRead;
    private RenderTexture _pressureWrite;
    private RenderTexture _divergence;
    private RenderTexture _curl;
    private RenderTexture _optical;

    private int _initializeKernel;
    private int _primeKernel;
    private int _advectVelocityKernel;
    private int _applyForcesKernel;
    private int _curlKernel;
    private int _vorticityKernel;
    private int _divergenceKernel;
    private int _pressureKernel;
    private int _projectKernel;
    private int _advectThermalKernel;
    private int _injectThermalKernel;
    private int _buildOpticalKernel;

    private float _elapsed;
    private float _lastWeatherIntensity;
    private int _emitterCount;

    internal bool IsAvailable { get; private set; }
    internal Texture ThermalTexture => IsAvailable ? _thermalRead : Texture2D.blackTexture;
    internal Texture VelocityTexture => IsAvailable ? _velocityRead : Texture2D.blackTexture;
    internal Texture OpticalTexture => IsAvailable ? _optical : Texture2D.blackTexture;
    internal int EmitterCount => _emitterCount;

    internal HeatWaveThermalSimulation(Room room, HeatWaveTerrainField terrain)
    {
        _room = room ?? throw new ArgumentNullException(nameof(room));
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));

        if (!SystemInfo.supportsComputeShaders ||
            DryCycleShaderAssets.HeatWaveThermalCompute == null)
        {
            return;
        }

        try
        {
            _solver = UnityEngine.Object.Instantiate(DryCycleShaderAssets.HeatWaveThermalCompute);
            CacheKernels();
            AllocateTextures();
            InitializeFields();
            IsAvailable = true;

            Plugin.Logger?.LogInfo(
                $"DryCycle HeatWave thermal field initialized for " +
                $"'{room.abstractRoom?.name ?? "unknown"}': " +
                $"{_thermalRead.width}x{_thermalRead.height}, " +
                $"pressureIterations={PressureIterations}, emitters={MaxEmitters} max.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle HeatWave GPU simulation disabled for " +
                $"'{room.abstractRoom?.name ?? "unknown"}'.");
            Plugin.Logger?.LogWarning(ex);
            Dispose();
        }
    }

    internal void Step(
        float deltaTime,
        float weatherIntensity,
        float solarIntensity,
        HeatWaveBurstController burst)
    {
        if (!IsAvailable || _solver == null)
        {
            return;
        }

        float dt = Mathf.Clamp(deltaTime, 1f / 240f, 1f / 20f);
        float intensity = Mathf.Clamp01(weatherIntensity);
        float solar = Mathf.Clamp01(solarIntensity);
        _elapsed += dt;

        BuildEmitterData();
        SetCommonParameters(dt, intensity, solar, burst);

        // Entering a room during an already-established HeatWave must not reveal the
        // implementation by showing a cold, motionless field for several seconds.
        if (_lastWeatherIntensity <= 0.025f && intensity > 0.08f)
        {
            PrimeActiveWeather();
        }
        _lastWeatherIntensity = intensity;

        BindVelocityPair(_advectVelocityKernel);
        Dispatch(_advectVelocityKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        BindVelocityPair(_applyForcesKernel);
        _solver.SetTexture(_applyForcesKernel, "_ThermalRead", _thermalRead);
        Dispatch(_applyForcesKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        _solver.SetTexture(_curlKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_curlKernel, "_CurlWrite", _curl);
        _solver.SetTexture(_curlKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_curlKernel);

        BindVelocityPair(_vorticityKernel);
        _solver.SetTexture(_vorticityKernel, "_CurlRead", _curl);
        _solver.SetTexture(_vorticityKernel, "_ThermalRead", _thermalRead);
        Dispatch(_vorticityKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        _solver.SetTexture(_divergenceKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_divergenceKernel, "_DivergenceWrite", _divergence);
        _solver.SetTexture(_divergenceKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_divergenceKernel);

        for (int i = 0; i < PressureIterations; i++)
        {
            _solver.SetTexture(_pressureKernel, "_PressureRead", _pressureRead);
            _solver.SetTexture(_pressureKernel, "_PressureWrite", _pressureWrite);
            _solver.SetTexture(_pressureKernel, "_DivergenceRead", _divergence);
            _solver.SetTexture(_pressureKernel, "_TerrainTex", _terrain.Texture);
            Dispatch(_pressureKernel);
            Swap(ref _pressureRead, ref _pressureWrite);
        }

        BindVelocityPair(_projectKernel);
        _solver.SetTexture(_projectKernel, "_PressureRead", _pressureRead);
        Dispatch(_projectKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        _solver.SetTexture(_advectThermalKernel, "_ThermalRead", _thermalRead);
        _solver.SetTexture(_advectThermalKernel, "_ThermalWrite", _thermalWrite);
        _solver.SetTexture(_advectThermalKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_advectThermalKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_advectThermalKernel);
        Swap(ref _thermalRead, ref _thermalWrite);

        _solver.SetTexture(_injectThermalKernel, "_ThermalRead", _thermalRead);
        _solver.SetTexture(_injectThermalKernel, "_ThermalWrite", _thermalWrite);
        _solver.SetTexture(_injectThermalKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_injectThermalKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_injectThermalKernel);
        Swap(ref _thermalRead, ref _thermalWrite);

        _solver.SetTexture(_buildOpticalKernel, "_ThermalRead", _thermalRead);
        _solver.SetTexture(_buildOpticalKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_buildOpticalKernel, "_CurlRead", _curl);
        _solver.SetTexture(_buildOpticalKernel, "_TerrainTex", _terrain.Texture);
        _solver.SetTexture(_buildOpticalKernel, "_OpticalWrite", _optical);
        Dispatch(_buildOpticalKernel);
    }

    public void Dispose()
    {
        IsAvailable = false;
        Release(ref _velocityRead);
        Release(ref _velocityWrite);
        Release(ref _thermalRead);
        Release(ref _thermalWrite);
        Release(ref _pressureRead);
        Release(ref _pressureWrite);
        Release(ref _divergence);
        Release(ref _curl);
        Release(ref _optical);

        if (_solver != null)
        {
            UnityEngine.Object.Destroy(_solver);
            _solver = null;
        }
    }

    private void CacheKernels()
    {
        _initializeKernel = _solver.FindKernel("InitializeFields");
        _primeKernel = _solver.FindKernel("PrimeActiveWeather");
        _advectVelocityKernel = _solver.FindKernel("AdvectVelocity");
        _applyForcesKernel = _solver.FindKernel("ApplyThermalForces");
        _curlKernel = _solver.FindKernel("ComputeCurl");
        _vorticityKernel = _solver.FindKernel("ApplyVorticity");
        _divergenceKernel = _solver.FindKernel("ComputeDivergence");
        _pressureKernel = _solver.FindKernel("JacobiPressure");
        _projectKernel = _solver.FindKernel("ProjectVelocity");
        _advectThermalKernel = _solver.FindKernel("AdvectThermal");
        _injectThermalKernel = _solver.FindKernel("InjectThermal");
        _buildOpticalKernel = _solver.FindKernel("BuildOpticalField");
    }

    private void AllocateTextures()
    {
        int width = Mathf.Clamp(Math.Max(1, _room.TileWidth) * CellsPerTile, 64, MaxWidth);
        int height = Mathf.Clamp(Math.Max(1, _room.TileHeight) * CellsPerTile, 64, MaxHeight);

        _velocityRead = CreateField(width, height, "VelocityA");
        _velocityWrite = CreateField(width, height, "VelocityB");
        _thermalRead = CreateField(width, height, "ThermalA");
        _thermalWrite = CreateField(width, height, "ThermalB");
        _pressureRead = CreateField(width, height, "PressureA");
        _pressureWrite = CreateField(width, height, "PressureB");
        _divergence = CreateField(width, height, "Divergence");
        _curl = CreateField(width, height, "Curl");
        _optical = CreateField(width, height, "Optical");
    }

    private void InitializeFields()
    {
        BuildEmitterData();
        SetCommonParameters(1f / 40f, 0f, 0f, null);
        _solver.SetTexture(_initializeKernel, "_VelocityWrite", _velocityRead);
        _solver.SetTexture(_initializeKernel, "_ThermalWrite", _thermalRead);
        _solver.SetTexture(_initializeKernel, "_PressureWrite", _pressureRead);
        _solver.SetTexture(_initializeKernel, "_DivergenceWrite", _divergence);
        _solver.SetTexture(_initializeKernel, "_CurlWrite", _curl);
        _solver.SetTexture(_initializeKernel, "_OpticalWrite", _optical);
        _solver.SetTexture(_initializeKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_initializeKernel);

        Graphics.Blit(_velocityRead, _velocityWrite);
        Graphics.Blit(_thermalRead, _thermalWrite);
        Graphics.Blit(_pressureRead, _pressureWrite);
    }

    private void PrimeActiveWeather()
    {
        _solver.SetTexture(_primeKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_primeKernel, "_VelocityWrite", _velocityWrite);
        _solver.SetTexture(_primeKernel, "_ThermalRead", _thermalRead);
        _solver.SetTexture(_primeKernel, "_ThermalWrite", _thermalWrite);
        _solver.SetTexture(_primeKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_primeKernel);
        Swap(ref _velocityRead, ref _velocityWrite);
        Swap(ref _thermalRead, ref _thermalWrite);

        // Prime both ping-pong targets so the first advection pass cannot sample the
        // stale cold texture after a swap.
        Graphics.Blit(_velocityRead, _velocityWrite);
        Graphics.Blit(_thermalRead, _thermalWrite);
    }

    private void SetCommonParameters(
        float dt,
        float weatherIntensity,
        float solarIntensity,
        HeatWaveBurstController burst)
    {
        float intensity = Mathf.Clamp01(weatherIntensity);
        float solar = Mathf.Clamp01(solarIntensity);
        float stillness = burst?.Stillness ?? 0f;
        float burstStrength = burst?.BurstStrength ?? 0f;
        float burstKick = burst?.BurstKick ?? 0f;

        _solver.SetFloat("_DeltaTime", dt);
        _solver.SetFloat("_SimulationTime", _elapsed);
        _solver.SetFloat("_HeatIntensity", intensity);
        _solver.SetFloat("_RoomSolarIntensity", solar);
        _solver.SetFloat("_VelocityDissipation", Mathf.Lerp(0.994f, 0.9985f, stillness));
        _solver.SetFloat("_ThermalDissipation", 0.9985f);
        _solver.SetFloat("_BuoyancyScale", burst?.BuoyancyScale ?? 1f);
        _solver.SetFloat("_TurbulenceScale", burst?.TurbulenceScale ?? 1f);
        _solver.SetFloat("_HeatStorageScale", burst?.HeatStorageScale ?? 1f);
        _solver.SetFloat("_BurstStrength", burstStrength);
        _solver.SetFloat("_BurstKick", burstKick);
        _solver.SetFloat("_Stillness", stillness);
        _solver.SetVector("_RoomSizePx", new Vector4(
            _terrain.RoomSizePixels.x,
            _terrain.RoomSizePixels.y,
            0f,
            0f));
        _solver.SetInt("_EmitterCount", _emitterCount);
        _solver.SetVectorArray("_EmitterStartRadius", _emitterStartRadius);
        _solver.SetVectorArray("_EmitterEndStrength", _emitterEndStrength);
        _solver.SetVectorArray("_EmitterFlow", _emitterFlow);
        _solver.SetVectorArray("_EmitterShape", _emitterShape);

        Water water = _room?.waterObject;
        bool hasWater = water != null;
        _solver.SetFloat("_HasWater", hasWater ? 1f : 0f);
        _solver.SetFloat("_WaterLevelPx", hasWater ? water.fWaterLevel : -100000f);
        _solver.SetFloat("_WaterInverted", hasWater && _room.waterInverted ? 1f : 0f);
    }

    private void BuildEmitterData()
    {
        HeatColumnHooks.CollectEmitters(_room, _emitters);
        _emitterCount = Math.Min(MaxEmitters, _emitters.Count);
        Array.Clear(_emitterStartRadius, 0, _emitterStartRadius.Length);
        Array.Clear(_emitterEndStrength, 0, _emitterEndStrength.Length);
        Array.Clear(_emitterFlow, 0, _emitterFlow.Length);
        Array.Clear(_emitterShape, 0, _emitterShape.Length);

        Vector2 roomSize = _terrain.RoomSizePixels;
        for (int i = 0; i < _emitterCount; i++)
        {
            HeatColumnEmitterSample emitter = _emitters[i];
            Vector2 startUv = new(
                emitter.Start.x / Mathf.Max(1f, roomSize.x),
                emitter.Start.y / Mathf.Max(1f, roomSize.y));
            Vector2 endUv = new(
                emitter.End.x / Mathf.Max(1f, roomSize.x),
                emitter.End.y / Mathf.Max(1f, roomSize.y));

            Vector2 direction = emitter.End - emitter.Start;
            float length = Mathf.Max(1f, direction.magnitude);
            direction /= length;

            // Authored reach and airflow speed are intentionally separate. A long
            // lazily rising column is now possible without forcing huge velocity.
            float speedPx = 82f * emitter.FlowSpeed;
            Vector2 flowUv = new(
                direction.x * speedPx / Mathf.Max(1f, roomSize.x),
                direction.y * speedPx / Mathf.Max(1f, roomSize.y));

            _emitterStartRadius[i] = new Vector4(
                startUv.x,
                startUv.y,
                emitter.Radius,
                length);
            _emitterEndStrength[i] = new Vector4(
                endUv.x,
                endUv.y,
                emitter.Strength,
                emitter.Turbulence);
            _emitterFlow[i] = new Vector4(
                flowUv.x,
                flowUv.y,
                Mathf.Repeat(i * 0.6180339f, 1f),
                emitter.Pulse);
            _emitterShape[i] = new Vector4(
                emitter.Expansion,
                emitter.FlowSpeed,
                emitter.Pulse,
                0f);
        }
    }

    private void BindVelocityPair(int kernel)
    {
        _solver.SetTexture(kernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(kernel, "_VelocityWrite", _velocityWrite);
        _solver.SetTexture(kernel, "_TerrainTex", _terrain.Texture);
    }

    private void Dispatch(int kernel)
    {
        int width = _velocityRead?.width ?? _optical.width;
        int height = _velocityRead?.height ?? _optical.height;
        _solver.Dispatch(
            kernel,
            Mathf.CeilToInt(width / 8f),
            Mathf.CeilToInt(height / 8f),
            1);
    }

    private static RenderTexture CreateField(int width, int height, string suffix)
    {
        RenderTexture texture = new(width, height, 0, RenderTextureFormat.ARGBHalf)
        {
            name = $"DryCycleHeatWave{suffix}",
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        texture.Create();
        if (!texture.IsCreated())
        {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidOperationException(
                $"Could not create DryCycle HeatWave field {suffix} at {width}x{height}.");
        }
        return texture;
    }

    private static void Swap(ref RenderTexture a, ref RenderTexture b)
    {
        (a, b) = (b, a);
    }

    private static void Release(ref RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }

        texture.Release();
        UnityEngine.Object.Destroy(texture);
        texture = null;
    }
}
