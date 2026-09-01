using System;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.Fog;

/// <summary>
/// Whole-room, obstacle-aware low-frequency fog simulation. Velocity is stored in
/// normalized room UV / second, while density is unitless. The simulation is purely
/// visual: DenseFog gameplay extinction is calculated independently in the final
/// composite shader, so fluid thinning can never reveal distant terrain by itself.
/// </summary>
internal sealed class DryCycleFogFluidSimulation : IDisposable
{
    private const int CellsPerTile = 4;
    private const int MaxWidth = 1024;
    private const int MaxHeight = 512;
    private const int PressureIterations = 28;
    private const int MaxPlayerImpulses = 4;

    private readonly Room _room;
    private readonly DryCycleFogObstacleField _obstacles;
    private readonly Vector4[] _impulsePosRadius = new Vector4[MaxPlayerImpulses];
    private readonly Vector4[] _impulseVelocity = new Vector4[MaxPlayerImpulses];

    private ComputeShader _solver;
    private RenderTexture _velocityRead;
    private RenderTexture _velocityWrite;
    private RenderTexture _densityRead;
    private RenderTexture _densityWrite;
    private RenderTexture _pressureRead;
    private RenderTexture _pressureWrite;
    private RenderTexture _divergence;
    private RenderTexture _curl;

    private int _initializeKernel;
    private int _advectVelocityKernel;
    private int _applyForcesKernel;
    private int _curlKernel;
    private int _vorticityKernel;
    private int _divergenceKernel;
    private int _pressureKernel;
    private int _projectKernel;
    private int _advectDensityKernel;
    private int _maintainDensityKernel;

    private float _elapsed;
    private int _impulseCount;

    internal bool IsAvailable { get; private set; }
    internal Texture DensityTexture => IsAvailable ? _densityRead : Texture2D.whiteTexture;
    internal Texture ObstacleTexture => _obstacles?.Texture ?? Texture2D.blackTexture;
    internal Vector2 RoomSizePixels => _obstacles?.RoomSizePixels ?? Vector2.one;

    internal DryCycleFogFluidSimulation(
        Room room,
        DryCycleFogObstacleField obstacles)
    {
        _room = room ?? throw new ArgumentNullException(nameof(room));
        _obstacles = obstacles ?? throw new ArgumentNullException(nameof(obstacles));

        if (!SystemInfo.supportsComputeShaders ||
            DryCycleShaderAssets.FogFluidCompute == null)
        {
            return;
        }

        try
        {
            _solver = UnityEngine.Object.Instantiate(DryCycleShaderAssets.FogFluidCompute);
            CacheKernels();
            AllocateTextures();
            InitializeFields();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning(
                $"DryCycle fog fluid simulation disabled for room " +
                $"'{room.abstractRoom?.name ?? "unknown"}'.");
            Plugin.Logger?.LogWarning(ex);
            Dispose();
        }
    }

    internal void Step(float deltaTime, float fogStrength, float denseStrength)
    {
        if (!IsAvailable || _solver == null)
        {
            return;
        }

        float dt = Mathf.Clamp(deltaTime, 1f / 240f, 1f / 20f);
        _elapsed += dt;

        BuildPlayerImpulses();

        Vector2 roomSize = RoomSizePixels;
        Vector2 windUvPerSecond = new(
            (18f + Mathf.Sin(_elapsed * 0.073f) * 4f) / Mathf.Max(1f, roomSize.x),
            (2.5f + Mathf.Cos(_elapsed * 0.049f) * 2f) / Mathf.Max(1f, roomSize.y));

        float dense = Mathf.Clamp01(denseStrength);
        float targetDensity = Mathf.Lerp(0.50f, 0.82f, dense);
        float weatherPresence = Mathf.Clamp01(Mathf.Max(fogStrength, denseStrength));
        targetDensity = Mathf.Lerp(0.38f, targetDensity, weatherPresence);

        SetCommonParameters(dt, windUvPerSecond, targetDensity);

        BindVelocityPair(_advectVelocityKernel);
        Dispatch(_advectVelocityKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        BindVelocityPair(_applyForcesKernel);
        Dispatch(_applyForcesKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        _solver.SetTexture(_curlKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_curlKernel, "_CurlWrite", _curl);
        _solver.SetTexture(_curlKernel, "_ObstacleTex", _obstacles.Texture);
        Dispatch(_curlKernel);

        BindVelocityPair(_vorticityKernel);
        _solver.SetTexture(_vorticityKernel, "_CurlRead", _curl);
        Dispatch(_vorticityKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        _solver.SetTexture(_divergenceKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_divergenceKernel, "_DivergenceWrite", _divergence);
        _solver.SetTexture(_divergenceKernel, "_ObstacleTex", _obstacles.Texture);
        Dispatch(_divergenceKernel);

        for (int i = 0; i < PressureIterations; i++)
        {
            _solver.SetTexture(_pressureKernel, "_PressureRead", _pressureRead);
            _solver.SetTexture(_pressureKernel, "_PressureWrite", _pressureWrite);
            _solver.SetTexture(_pressureKernel, "_DivergenceRead", _divergence);
            _solver.SetTexture(_pressureKernel, "_ObstacleTex", _obstacles.Texture);
            Dispatch(_pressureKernel);
            Swap(ref _pressureRead, ref _pressureWrite);
        }

        BindVelocityPair(_projectKernel);
        _solver.SetTexture(_projectKernel, "_PressureRead", _pressureRead);
        Dispatch(_projectKernel);
        Swap(ref _velocityRead, ref _velocityWrite);

        _solver.SetTexture(_advectDensityKernel, "_DensityRead", _densityRead);
        _solver.SetTexture(_advectDensityKernel, "_DensityWrite", _densityWrite);
        _solver.SetTexture(_advectDensityKernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(_advectDensityKernel, "_ObstacleTex", _obstacles.Texture);
        Dispatch(_advectDensityKernel);
        Swap(ref _densityRead, ref _densityWrite);

        _solver.SetTexture(_maintainDensityKernel, "_DensityRead", _densityRead);
        _solver.SetTexture(_maintainDensityKernel, "_DensityWrite", _densityWrite);
        _solver.SetTexture(_maintainDensityKernel, "_ObstacleTex", _obstacles.Texture);
        Dispatch(_maintainDensityKernel);
        Swap(ref _densityRead, ref _densityWrite);
    }

    public void Dispose()
    {
        IsAvailable = false;

        Release(ref _velocityRead);
        Release(ref _velocityWrite);
        Release(ref _densityRead);
        Release(ref _densityWrite);
        Release(ref _pressureRead);
        Release(ref _pressureWrite);
        Release(ref _divergence);
        Release(ref _curl);

        if (_solver != null)
        {
            UnityEngine.Object.Destroy(_solver);
            _solver = null;
        }
    }

    private void CacheKernels()
    {
        _initializeKernel = _solver.FindKernel("InitializeFields");
        _advectVelocityKernel = _solver.FindKernel("AdvectVelocity");
        _applyForcesKernel = _solver.FindKernel("ApplyForces");
        _curlKernel = _solver.FindKernel("ComputeCurl");
        _vorticityKernel = _solver.FindKernel("ApplyVorticity");
        _divergenceKernel = _solver.FindKernel("ComputeDivergence");
        _pressureKernel = _solver.FindKernel("JacobiPressure");
        _projectKernel = _solver.FindKernel("ProjectVelocity");
        _advectDensityKernel = _solver.FindKernel("AdvectDensity");
        _maintainDensityKernel = _solver.FindKernel("MaintainDensity");
    }

    private void AllocateTextures()
    {
        int width = Mathf.Clamp(
            Math.Max(1, _room.TileWidth) * CellsPerTile,
            64,
            MaxWidth);
        int height = Mathf.Clamp(
            Math.Max(1, _room.TileHeight) * CellsPerTile,
            64,
            MaxHeight);

        _velocityRead = CreateField(width, height, "VelocityA");
        _velocityWrite = CreateField(width, height, "VelocityB");
        _densityRead = CreateField(width, height, "DensityA");
        _densityWrite = CreateField(width, height, "DensityB");
        _pressureRead = CreateField(width, height, "PressureA");
        _pressureWrite = CreateField(width, height, "PressureB");
        _divergence = CreateField(width, height, "Divergence");
        _curl = CreateField(width, height, "Curl");
    }

    private void InitializeFields()
    {
        SetCommonParameters(
            1f / 40f,
            new Vector2(18f / Mathf.Max(1f, RoomSizePixels.x), 0f),
            0.55f);

        _solver.SetTexture(_initializeKernel, "_VelocityWrite", _velocityRead);
        _solver.SetTexture(_initializeKernel, "_DensityWrite", _densityRead);
        _solver.SetTexture(_initializeKernel, "_PressureWrite", _pressureRead);
        _solver.SetTexture(_initializeKernel, "_DivergenceWrite", _divergence);
        _solver.SetTexture(_initializeKernel, "_CurlWrite", _curl);
        _solver.SetTexture(_initializeKernel, "_ObstacleTex", _obstacles.Texture);
        Dispatch(_initializeKernel);

        Graphics.Blit(_velocityRead, _velocityWrite);
        Graphics.Blit(_densityRead, _densityWrite);
        Graphics.Blit(_pressureRead, _pressureWrite);
    }

    private void SetCommonParameters(
        float dt,
        Vector2 windUvPerSecond,
        float targetDensity)
    {
        _solver.SetFloat("_DeltaTime", dt);
        _solver.SetFloat("_SimulationTime", _elapsed);
        _solver.SetFloat("_TargetDensity", targetDensity);
        _solver.SetFloat("_VelocityDissipation", 0.994f);
        _solver.SetFloat("_DensityDissipation", 0.9975f);
        _solver.SetFloat("_DensityRelaxation", 0.18f);
        _solver.SetFloat("_VorticityStrength", 0.28f);
        _solver.SetVector("_WindVelocity", new Vector4(
            windUvPerSecond.x,
            windUvPerSecond.y,
            0f,
            0f));
        _solver.SetVector("_RoomSizePx", new Vector4(
            RoomSizePixels.x,
            RoomSizePixels.y,
            0f,
            0f));
        _solver.SetInt("_ImpulseCount", _impulseCount);
        _solver.SetVectorArray("_ImpulsePosRadius", _impulsePosRadius);
        _solver.SetVectorArray("_ImpulseVelocity", _impulseVelocity);
    }

    private void BuildPlayerImpulses()
    {
        _impulseCount = 0;
        Array.Clear(_impulsePosRadius, 0, _impulsePosRadius.Length);
        Array.Clear(_impulseVelocity, 0, _impulseVelocity.Length);

        if (_room?.game?.Players == null)
        {
            return;
        }

        Vector2 roomSize = RoomSizePixels;
        for (int i = 0;
             i < _room.game.Players.Count && _impulseCount < MaxPlayerImpulses;
             i++)
        {
            Player player = _room.game.Players[i]?.realizedCreature as Player;
            if (player?.room != _room ||
                player.bodyChunks == null ||
                player.bodyChunks.Length == 0)
            {
                continue;
            }

            Vector2 position = Vector2.zero;
            Vector2 velocity = Vector2.zero;
            int chunks = 0;
            for (int chunkIndex = 0; chunkIndex < player.bodyChunks.Length; chunkIndex++)
            {
                BodyChunk chunk = player.bodyChunks[chunkIndex];
                if (chunk == null)
                {
                    continue;
                }

                position += chunk.pos;
                velocity += chunk.vel;
                chunks++;
            }

            if (chunks == 0)
            {
                continue;
            }

            position /= chunks;
            velocity /= chunks;

            float speed = velocity.magnitude;
            float strength = Mathf.Clamp01(speed / 8f);
            if (strength <= 0.025f)
            {
                continue;
            }

            int index = _impulseCount++;
            _impulsePosRadius[index] = new Vector4(
                position.x / Mathf.Max(1f, roomSize.x),
                position.y / Mathf.Max(1f, roomSize.y),
                Mathf.Lerp(42f, 90f, strength),
                strength);

            _impulseVelocity[index] = new Vector4(
                velocity.x * 40f / Mathf.Max(1f, roomSize.x),
                velocity.y * 40f / Mathf.Max(1f, roomSize.y),
                0f,
                0f);
        }
    }

    private void BindVelocityPair(int kernel)
    {
        _solver.SetTexture(kernel, "_VelocityRead", _velocityRead);
        _solver.SetTexture(kernel, "_VelocityWrite", _velocityWrite);
        _solver.SetTexture(kernel, "_ObstacleTex", _obstacles.Texture);
    }

    private void Dispatch(int kernel)
    {
        int groupsX = Mathf.CeilToInt(_velocityRead.width / 8f);
        int groupsY = Mathf.CeilToInt(_velocityRead.height / 8f);
        _solver.Dispatch(kernel, groupsX, groupsY, 1);
    }

    private static RenderTexture CreateField(int width, int height, string suffix)
    {
        RenderTexture texture = new(width, height, 0, RenderTextureFormat.ARGBHalf)
        {
            name = $"DryCycleFog{suffix}",
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        texture.Create();
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
