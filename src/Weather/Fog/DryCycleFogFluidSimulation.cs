using System;
using System.Collections.Generic;
using DryCycle.Rendering;
using UnityEngine;

namespace DryCycle.Weather.Fog;

/// <summary>
/// Whole-room, obstacle-aware low-frequency fog simulation. Velocity is stored in
/// normalized room UV / second. Density texture R is physical fog density; G is an
/// explicit blast-clearing permission field. Ordinary advection/player wakes may alter
/// R but never gameplay visibility by themselves. Only recognized explosions write G,
/// allowing the final composite to make that local density loss temporarily real.
/// </summary>
internal sealed class DryCycleFogFluidSimulation : IDisposable
{
    // Five cells per 20px Rain World tile gives a 4px fluid cell in ordinary rooms.
    // This is deliberately denser than the old prototype because room-scale curls and
    // wakes are expected to survive all the way into the high-quality composite.
    private const int CellsPerTile = 5;
    private const int MaxWidth = 1280;
    private const int MaxHeight = 640;
    private const int PressureIterations = 36;
    private const int MaxPlayerImpulses = 4;
    private const int MaxBlastImpulses = 4;
    private const float BlastClearDecayPerSecond = 0.62f;

    private readonly Room _room;
    private readonly DryCycleFogObstacleField _obstacles;
    private readonly Vector4[] _impulsePosRadius = new Vector4[MaxPlayerImpulses];
    private readonly Vector4[] _impulseVelocity = new Vector4[MaxPlayerImpulses];
    private readonly Vector4[] _blastPosRadii = new Vector4[MaxBlastImpulses];
    private readonly Vector4[] _blastParameters = new Vector4[MaxBlastImpulses];
    private readonly HashSet<Explosion> _processedExplosions = new();

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
    private int _applyBlastVelocityKernel = -1;
    private int _advectDensityKernel;
    private int _maintainDensityKernel;
    private int _applyBlastDensityKernel = -1;

    private float _elapsed;
    private int _impulseCount;
    private int _blastCount;
    private bool _blastKernelsAvailable;

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

            Plugin.Logger?.LogInfo(
                $"DryCycle fog fluid initialized for " +
                $"'{room.abstractRoom?.name ?? "unknown"}': " +
                $"{_densityRead.width}x{_densityRead.height}, " +
                $"pressureIterations={PressureIterations}, " +
                $"blastInteraction={(_blastKernelsAvailable ? "yes" : "no")}.");

            if (!_blastKernelsAvailable)
            {
                Plugin.Logger?.LogWarning(
                    "DryCycle fog blast kernels are missing from the loaded weather " +
                    "AssetBundle. Base volumetric fog remains enabled, but explosive " +
                    "fog displacement requires rebuilding drycycleweather.");
            }
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
        BuildBlastImpulses();

        Vector2 roomSize = RoomSizePixels;
        // Broad prevailing drift. The speed changes slowly enough to feel atmospheric,
        // while the solver's local vorticity and obstacle field provide the turbulence.
        Vector2 windUvPerSecond = new(
            (22f + Mathf.Sin(_elapsed * 0.067f) * 5f) /
                Mathf.Max(1f, roomSize.x),
            (3.0f + Mathf.Cos(_elapsed * 0.043f) * 2.4f) /
                Mathf.Max(1f, roomSize.y));

        float dense = Mathf.Clamp01(denseStrength);
        float targetDensity = Mathf.Lerp(0.48f, 0.86f, dense);
        float weatherPresence = Mathf.Clamp01(Mathf.Max(fogStrength, denseStrength));
        targetDensity = Mathf.Lerp(0.34f, targetDensity, weatherPresence);

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

        // Explosion shock is intentionally injected after pressure projection. It is a
        // short compressible impulse; projecting it immediately would erase the radial
        // expansion and recreate the room-scale return-flow problem seen with oversized
        // player wakes. Terrain-normal components are still clamped in the blast kernel.
        if (_blastKernelsAvailable && _blastCount > 0)
        {
            BindVelocityPair(_applyBlastVelocityKernel);
            Dispatch(_applyBlastVelocityKernel);
            Swap(ref _velocityRead, ref _velocityWrite);
        }

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

        // Carve/pack density after ordinary maintenance so the explosion gets one clean
        // authoritative frame rather than being partially refilled by the inlet/relax pass.
        if (_blastKernelsAvailable && _blastCount > 0)
        {
            _solver.SetTexture(_applyBlastDensityKernel, "_DensityRead", _densityRead);
            _solver.SetTexture(_applyBlastDensityKernel, "_DensityWrite", _densityWrite);
            _solver.SetTexture(_applyBlastDensityKernel, "_ObstacleTex", _obstacles.Texture);
            Dispatch(_applyBlastDensityKernel);
            Swap(ref _densityRead, ref _densityWrite);
        }
    }

    public void Dispose()
    {
        IsAvailable = false;
        _processedExplosions.Clear();

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

        _applyBlastVelocityKernel = FindOptionalKernel("ApplyBlastVelocity");
        _applyBlastDensityKernel = FindOptionalKernel("ApplyBlastDensity");
        _blastKernelsAvailable =
            _applyBlastVelocityKernel >= 0 &&
            _applyBlastDensityKernel >= 0;
    }

    private int FindOptionalKernel(string name)
    {
        try
        {
            return _solver.FindKernel(name);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void AllocateTextures()
    {
        int width = Mathf.Clamp(
            Math.Max(1, _room.TileWidth) * CellsPerTile,
            80,
            MaxWidth);
        int height = Mathf.Clamp(
            Math.Max(1, _room.TileHeight) * CellsPerTile,
            80,
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
            new Vector2(22f / Mathf.Max(1f, RoomSizePixels.x), 0f),
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
        _solver.SetFloat("_VelocityDissipation", 0.997f);
        _solver.SetFloat("_DensityDissipation", 0.9990f);
        // Interior relaxation is intentionally weak. The compute pass injects most new
        // fog at the upwind room boundary so masses travel through the room instead of
        // materializing uniformly everywhere.
        _solver.SetFloat("_DensityRelaxation", 0.055f);
        _solver.SetFloat("_VorticityStrength", 0.62f);
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

        // Do not set unknown properties on an older bundle. Missing optional blast
        // kernels degrade only this new interaction, never the base fog simulation.
        if (_blastKernelsAvailable)
        {
            _solver.SetFloat("_BlastClearDecay", BlastClearDecayPerSecond);
            _solver.SetInt("_BlastCount", _blastCount);
            _solver.SetVectorArray("_BlastPosRadii", _blastPosRadii);
            _solver.SetVectorArray("_BlastParams", _blastParameters);
        }
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
            float strength = Mathf.Clamp01(speed / 7f);
            if (strength <= 0.02f)
            {
                continue;
            }

            int index = _impulseCount++;
            _impulsePosRadius[index] = new Vector4(
                position.x / Mathf.Max(1f, roomSize.x),
                position.y / Mathf.Max(1f, roomSize.y),
                Mathf.Lerp(46f, 104f, strength),
                strength);

            _impulseVelocity[index] = new Vector4(
                velocity.x * 40f / Mathf.Max(1f, roomSize.x),
                velocity.y * 40f / Mathf.Max(1f, roomSize.y),
                0f,
                0f);
        }
    }

    private void BuildBlastImpulses()
    {
        _blastCount = 0;
        Array.Clear(_blastPosRadii, 0, _blastPosRadii.Length);
        Array.Clear(_blastParameters, 0, _blastParameters.Length);

        if (!_blastKernelsAvailable || _room?.updateList == null)
        {
            return;
        }

        _processedExplosions.RemoveWhere(explosion =>
            explosion == null ||
            explosion.slatedForDeletetion ||
            explosion.room != _room);

        Vector2 roomSize = RoomSizePixels;
        for (int i = 0;
             i < _room.updateList.Count && _blastCount < MaxBlastImpulses;
             i++)
        {
            if (_room.updateList[i] is not Explosion explosion ||
                explosion.slatedForDeletetion ||
                explosion.room != _room ||
                _processedExplosions.Contains(explosion))
            {
                continue;
            }

            bool scavengerBomb = explosion.sourceObject is ScavengerBomb;
            bool explosiveSpear = explosion.sourceObject is ExplosiveSpear;
            if (!scavengerBomb && !explosiveSpear)
            {
                continue;
            }

            float sourceRadius = Mathf.Max(30f, explosion.rad);
            float coreRadius = sourceRadius * (scavengerBomb ? 0.60f : 0.58f);
            float outerRadius = sourceRadius * (scavengerBomb ? 1.18f : 1.30f);

            // The bomb's stock Explosion is 250px while the explosive spear is 110px.
            // These values preserve that hierarchy without letting either shock become a
            // room-wide wind field. Compression is approximately proportional to carved
            // core area, so the outer ring reads as displaced fog rather than new fog.
            float impulsePxPerSecond = scavengerBomb ? 620f : 360f;
            float densityCarve = scavengerBomb ? 0.96f : 0.87f;
            float compression = scavengerBomb ? 0.38f : 0.28f;
            float visibilityClear = scavengerBomb ? 0.98f : 0.88f;

            int index = _blastCount++;
            _blastPosRadii[index] = new Vector4(
                explosion.pos.x / Mathf.Max(1f, roomSize.x),
                explosion.pos.y / Mathf.Max(1f, roomSize.y),
                coreRadius,
                outerRadius);
            _blastParameters[index] = new Vector4(
                impulsePxPerSecond,
                densityCarve,
                compression,
                visibilityClear);

            _processedExplosions.Add(explosion);
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
        if (!texture.IsCreated())
        {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidOperationException(
                $"Could not create DryCycle fog field {suffix} at {width}x{height}.");
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
