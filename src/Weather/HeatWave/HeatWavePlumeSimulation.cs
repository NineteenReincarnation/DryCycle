using System;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.HeatWave;

/// <summary>
/// Persistent visual plume field driven by the physical thermal solver.
///
/// This is deliberately not a second gameplay fluid simulation. The thermal solver
/// remains authoritative for air/heat motion; this field extracts sparse, coherent
/// rising bodies suitable for presentation. R=density, G=hot core, B=age, A=stable
/// plume identity/phase. Strong refraction therefore exists only inside readable hot
/// air structures.
/// </summary>
internal sealed class HeatWavePlumeSimulation : IDisposable
{
    private const int CellsPerTile = 4;
    private const int MaxWidth = 1024;
    private const int MaxHeight = 512;
    private const int PrimeRelaxSteps = 8;
    private const float PrimeRelaxStepSeconds = 0.08f;

    private readonly Room _room;
    private readonly HeatWaveTerrainField _terrain;

    private ComputeShader _solver;
    private RenderTexture _read;
    private RenderTexture _write;
    private int _initializeKernel;
    private int _primeKernel;
    private int _advectKernel;
    private int _injectKernel;
    private float _elapsed;
    private float _lastIntensity;

    internal bool IsAvailable { get; private set; }
    internal Texture PlumeTexture => IsAvailable ? _read : Texture2D.blackTexture;

    internal HeatWavePlumeSimulation(Room room, HeatWaveTerrainField terrain)
    {
        _room = room ?? throw new ArgumentNullException(nameof(room));
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));

        if (!SystemInfo.supportsComputeShaders ||
            DryCycleShaderAssets.HeatWavePlumeCompute == null)
        {
            return;
        }

        try
        {
            _solver = UnityEngine.Object.Instantiate(DryCycleShaderAssets.HeatWavePlumeCompute);
            _initializeKernel = _solver.FindKernel("InitializePlumes");
            _primeKernel = _solver.FindKernel("PrimePlumes");
            _advectKernel = _solver.FindKernel("AdvectPlumes");
            _injectKernel = _solver.FindKernel("InjectPlumes");

            int width = Mathf.Clamp(Math.Max(1, _room.TileWidth) * CellsPerTile, 64, MaxWidth);
            int height = Mathf.Clamp(Math.Max(1, _room.TileHeight) * CellsPerTile, 64, MaxHeight);
            _read = CreateField(width, height, "PlumeA");
            _write = CreateField(width, height, "PlumeB");
            Initialize();
            IsAvailable = true;

            Plugin.Logger?.LogInfo(
                $"DryCycle HeatWave visual plume field initialized for " +
                $"'{room.abstractRoom?.name ?? "unknown"}': {width}x{height}.");
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle HeatWave plume field disabled for " +
                $"'{room.abstractRoom?.name ?? "unknown"}'.");
            Plugin.Logger?.LogWarning(ex);
            Dispose();
        }
    }

    internal void Step(
        float deltaTime,
        float weatherIntensity,
        float solarIntensity,
        Texture thermalTexture,
        Texture velocityTexture)
    {
        if (!IsAvailable ||
            _solver == null ||
            thermalTexture == null ||
            velocityTexture == null)
        {
            return;
        }

        float intensity = Mathf.Clamp01(weatherIntensity);
        float solar = Mathf.Clamp01(solarIntensity);

        // The previous implementation tried to "prime" by running ~1.5 seconds of the
        // normal stochastic birth cycle. Some lanes have much longer inactive phases,
        // so a room could legitimately enter HeatWave with an almost black plume field.
        // PrimePlumes writes an established but deterministic convection snapshot once;
        // a few normal steps then relax it into the live velocity field.
        if (_lastIntensity <= 0.025f && intensity > 0.08f)
        {
            Prime(intensity, solar, thermalTexture, velocityTexture);
        }
        _lastIntensity = intensity;

        float dt = Mathf.Clamp(deltaTime, 1f / 240f, 1f / 20f);
        Advance(dt, intensity, solar, thermalTexture, velocityTexture);
    }

    public void Dispose()
    {
        IsAvailable = false;
        Release(ref _read);
        Release(ref _write);

        if (_solver != null)
        {
            UnityEngine.Object.Destroy(_solver);
            _solver = null;
        }
    }

    private void Initialize()
    {
        SetCommonParameters(1f / 40f, 0f, 0f);
        _solver.SetTexture(_initializeKernel, "_PlumeWrite", _read);
        _solver.SetTexture(_initializeKernel, "_ThermalTex", Texture2D.blackTexture);
        _solver.SetTexture(_initializeKernel, "_VelocityTex", Texture2D.blackTexture);
        _solver.SetTexture(_initializeKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_initializeKernel);
        Graphics.Blit(_read, _write);
    }

    private void Prime(
        float intensity,
        float solar,
        Texture thermalTexture,
        Texture velocityTexture)
    {
        SetCommonParameters(PrimeRelaxStepSeconds, intensity, solar);
        _solver.SetTexture(_primeKernel, "_PlumeWrite", _read);
        _solver.SetTexture(_primeKernel, "_ThermalTex", thermalTexture);
        _solver.SetTexture(_primeKernel, "_VelocityTex", velocityTexture);
        _solver.SetTexture(_primeKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_primeKernel);
        Graphics.Blit(_read, _write);

        for (int i = 0; i < PrimeRelaxSteps; i++)
        {
            Advance(
                PrimeRelaxStepSeconds,
                intensity,
                solar,
                thermalTexture,
                velocityTexture);
        }
    }

    private void Advance(
        float dt,
        float intensity,
        float solar,
        Texture thermalTexture,
        Texture velocityTexture)
    {
        _elapsed += dt;
        SetCommonParameters(dt, intensity, solar);

        _solver.SetTexture(_advectKernel, "_PlumeRead", _read);
        _solver.SetTexture(_advectKernel, "_PlumeWrite", _write);
        _solver.SetTexture(_advectKernel, "_ThermalTex", thermalTexture);
        _solver.SetTexture(_advectKernel, "_VelocityTex", velocityTexture);
        _solver.SetTexture(_advectKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_advectKernel);
        Swap();

        _solver.SetTexture(_injectKernel, "_PlumeRead", _read);
        _solver.SetTexture(_injectKernel, "_PlumeWrite", _write);
        _solver.SetTexture(_injectKernel, "_ThermalTex", thermalTexture);
        _solver.SetTexture(_injectKernel, "_VelocityTex", velocityTexture);
        _solver.SetTexture(_injectKernel, "_TerrainTex", _terrain.Texture);
        Dispatch(_injectKernel);
        Swap();
    }

    private void SetCommonParameters(
        float dt,
        float weatherIntensity,
        float solarIntensity)
    {
        _solver.SetFloat("_DeltaTime", dt);
        _solver.SetFloat("_SimulationTime", _elapsed);
        _solver.SetFloat("_HeatIntensity", Mathf.Clamp01(weatherIntensity));
        _solver.SetFloat("_RoomSolarIntensity", Mathf.Clamp01(solarIntensity));
        _solver.SetVector("_RoomSizePx", new Vector4(
            _terrain.RoomSizePixels.x,
            _terrain.RoomSizePixels.y,
            0f,
            0f));

        Water water = _room?.waterObject;
        bool hasWater = water != null;
        _solver.SetFloat("_HasWater", hasWater ? 1f : 0f);
        _solver.SetFloat("_WaterLevelPx", hasWater ? water.fWaterLevel : -100000f);
        _solver.SetFloat("_WaterInverted", hasWater && _room.waterInverted ? 1f : 0f);
    }

    private void Dispatch(int kernel)
    {
        if (_write == null)
        {
            return;
        }

        int groupsX = Mathf.CeilToInt(_write.width / 8f);
        int groupsY = Mathf.CeilToInt(_write.height / 8f);
        _solver.Dispatch(kernel, groupsX, groupsY, 1);
    }

    private RenderTexture CreateField(int width, int height, string suffix)
    {
        RenderTexture texture = new(width, height, 0, RenderTextureFormat.ARGBHalf)
        {
            name = $"DryCycleHeatWave{suffix}_{_room.abstractRoom?.name ?? "Room"}",
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        texture.Create();
        return texture;
    }

    private void Swap()
    {
        RenderTexture temp = _read;
        _read = _write;
        _write = temp;
    }

    private static void Release(ref RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }

        if (texture.IsCreated())
        {
            texture.Release();
        }
        UnityEngine.Object.Destroy(texture);
        texture = null;
    }
}
