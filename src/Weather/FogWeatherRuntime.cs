using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Rendering;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Fog;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// WorldClock-driven Fog / DenseFog renderer.
///
/// When DryCycle's weather AssetBundle is available, the late GrabShaders layer is a
/// full scene composite rather than an alpha veil. Gameplay transmittance, volumetric
/// visual density, room obstacles, fixed Lantern/LanternMouse reveal and pseudo-depth
/// are evaluated per pixel. A whole-room GPU fluid field provides low-frequency motion;
/// a runtime-generated 3D Perlin/Worley-style volume supplies erosion and billow detail.
///
/// If the custom bundle is absent, incompatible or a room-specific GPU resource cannot
/// be created, the previous palette-colored mesh remains as a safe compatibility
/// renderer. Mapper-authored RoomSettings Fog is never replaced: Rain World's own fog
/// renders normally and DryCycle layers on top.
/// </summary>
internal static class FogWeatherRuntime
{
    private const float Epsilon = 0.0001f;
    private const float FogNativeStrength = 0.92f;
    private const float DenseFogNativeStrength = 1.00f;

    // Compatibility-renderer values only. The custom composite computes physical-ish
    // exponential transmittance directly and does not use these alpha constants.
    private const float FogFallbackVeilStrength = 0.48f;
    private const float DenseFogFallbackVeilStrength = 0.94f;
    private const float DenseFogFallbackContrastExponent = 2.20f;

    private const float DenseFogPlayerAwarenessRadius = 58f;
    private const float DenseFogPlayerAwarenessStrength = 0.84f;

    // Fixed by design: native light radius animation must never make the fog opening
    // breathe/twitch. These are the minimum intended radii previously agreed on.
    private const float LanternRevealRadius = 200f;
    private const float LanternMouseRevealRadius = 40f;

    private const int MaxFogLights = 8;
    private const int MaxAwarenessSources = 4;
    private const int FallbackVeilColumns = 40;
    private const int FallbackVeilRows = 23;
    private const int FogEntityRefreshTicks = 10;

    private static readonly MaterialPropertyBlock MaterialProperties = new();
    private static readonly int FogColorId = Shader.PropertyToID("_DryCycleFogColor");
    private static readonly int RoomSizeId = Shader.PropertyToID("_DryCycleRoomSizePx");
    private static readonly int FogIntensityId = Shader.PropertyToID("_DryCycleFogIntensity");
    private static readonly int DenseFogIntensityId = Shader.PropertyToID("_DryCycleDenseFogIntensity");
    private static readonly int FogTimeId = Shader.PropertyToID("_DryCycleFogTime");
    private static readonly int FluidTextureId = Shader.PropertyToID("_DryCycleFogDensityTex");
    private static readonly int ObstacleTextureId = Shader.PropertyToID("_DryCycleFogObstacleTex");
    private static readonly int Noise3DId = Shader.PropertyToID("_DryCycleFogNoise3D");
    private static readonly int HasFluidId = Shader.PropertyToID("_DryCycleHasFluid");
    private static readonly int HasNoise3DId = Shader.PropertyToID("_DryCycleHasNoise3D");
    private static readonly int FogLightCountId = Shader.PropertyToID("_DryCycleFogLightCount");
    private static readonly int FogLightsId = Shader.PropertyToID("_DryCycleFogLights");
    private static readonly int FogLightColorsId = Shader.PropertyToID("_DryCycleFogLightColors");
    private static readonly int AwarenessCountId = Shader.PropertyToID("_DryCycleAwarenessCount");
    private static readonly int AwarenessId = Shader.PropertyToID("_DryCycleAwareness");

    private readonly struct RevealSample
    {
        internal readonly Vector2 Position;
        internal readonly float Radius;
        internal readonly float Strength;

        internal RevealSample(Vector2 position, float radius, float strength)
        {
            Position = position;
            Radius = radius;
            Strength = strength;
        }
    }

    private sealed class FogWeatherController : CosmeticSprite, INotifyWhenRoomUnloaded
    {
        private readonly List<Lantern> _lanterns = new();
        private readonly List<LanternMouse> _lanternMice = new();
        private readonly List<RevealSample> _fallbackRevealSamples = new();
        private readonly Vector4[] _fogLightData = new Vector4[MaxFogLights];
        private readonly Vector4[] _fogLightColors = new Vector4[MaxFogLights];
        private readonly Vector4[] _awarenessData = new Vector4[MaxAwarenessSources];

        private bool _useVolumetricComposite;
        private DryCycleFogObstacleField _obstacles;
        private DryCycleFogFluidSimulation _fluid;
        private bool _volumetricResourcesDisposed;

        private float _lastFogStrength;
        private float _fogStrength;
        private float _lastDenseStrength;
        private float _denseStrength;
        private float _lastNativeStrength;
        private float _nativeStrength;
        private float _lastFallbackVeilStrength;
        private float _fallbackVeilStrength;
        private float _visualTime;
        private int _entityRefreshCounter;
        private Color _fogColor = new(0.7f, 0.72f, 0.72f);

        internal FogWeatherController(Room room)
        {
            this.room = room;
            _useVolumetricComposite = DryCycleShaderAssets.HasFogComposite;

            if (!_useVolumetricComposite)
            {
                return;
            }

            try
            {
                _obstacles = new DryCycleFogObstacleField(room);
                DryCycleFogVolumeNoise.Ensure();
                _fluid = new DryCycleFogFluidSimulation(room, _obstacles);
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogError(
                    $"DryCycle could not construct volumetric fog resources for " +
                    $"'{room?.abstractRoom?.name ?? "unknown"}'. " +
                    "This room will use the compatibility fog renderer.");
                Plugin.Logger?.LogError(ex);
                DisposeVolumetricResources();
                _useVolumetricComposite = false;
            }
        }

        public override void Update(bool eu)
        {
            base.Update(eu);

            if (!_enabled)
            {
                Destroy();
                return;
            }

            _lastFogStrength = _fogStrength;
            _lastDenseStrength = _denseStrength;
            _lastNativeStrength = _nativeStrength;
            _lastFallbackVeilStrength = _fallbackVeilStrength;

            if (!TryEvaluate(room, out float fog, out float denseFog))
            {
                _fogStrength = 0f;
                _denseStrength = 0f;
                _nativeStrength = 0f;
                _fallbackVeilStrength = 0f;
                return;
            }

            _fogStrength = Mathf.Clamp01(fog);
            _denseStrength = Mathf.Clamp01(denseFog);
            _nativeStrength = Mathf.Clamp01(
                _fogStrength * FogNativeStrength +
                _denseStrength * DenseFogNativeStrength);
            _fallbackVeilStrength = Mathf.Clamp01(
                _fogStrength * FogFallbackVeilStrength +
                _denseStrength * DenseFogFallbackVeilStrength);

            _visualTime += 1f / 40f;

            if (--_entityRefreshCounter <= 0)
            {
                RefreshFogEntities();
                _entityRefreshCounter = FogEntityRefreshTicks;
            }

            if (_useVolumetricComposite && _fluid?.IsAvailable == true)
            {
                _fluid.Step(1f / 40f, _fogStrength, _denseStrength);
            }
        }

        public override void InitiateSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam)
        {
            base.InitiateSprites(sLeaser, rCam);

            float screenWidth = rCam.game.rainWorld.options.ScreenSize.x;
            float screenHeight = rCam.game.rainWorld.options.ScreenSize.y;

            FSprite nativeFog = new("Futile_White")
            {
                anchorX = 0f,
                anchorY = 0f,
                scaleX = screenWidth / 16f,
                scaleY = screenHeight / 16f,
                shader = rCam.game.rainWorld.Shaders["Fog"],
                alpha = 0f,
                isVisible = false
            };

            FSprite lateFog;
            if (_useVolumetricComposite)
            {
                lateFog = new FSprite("Futile_White")
                {
                    anchorX = 0f,
                    anchorY = 0f,
                    scaleX = screenWidth / 16f,
                    scaleY = screenHeight / 16f,
                    shader = DryCycleShaderAssets.FogComposite,
                    alpha = 1f,
                    isVisible = false
                };
            }
            else
            {
                TriangleMesh fallback = CreateFallbackVeilMesh(screenWidth, screenHeight);
                fallback.shader = rCam.game.rainWorld.Shaders["Basic"];
                fallback.isVisible = false;
                lateFog = fallback;
            }

            sLeaser.sprites = new[] { nativeFog, lateFog };
            AddToContainer(sLeaser, rCam, null);
        }

        public override void DrawSprites(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            float timeStacker,
            Vector2 camPos)
        {
            if (room == null || room != rCam.room)
            {
                sLeaser.CleanSpritesAndRemove();
                return;
            }

            float fogStrength = Mathf.Lerp(
                _lastFogStrength,
                _fogStrength,
                timeStacker);
            float denseStrength = Mathf.Lerp(
                _lastDenseStrength,
                _denseStrength,
                timeStacker);
            float nativeStrength = Mathf.Lerp(
                _lastNativeStrength,
                _nativeStrength,
                timeStacker);
            float fallbackVeilStrength = Mathf.Lerp(
                _lastFallbackVeilStrength,
                _fallbackVeilStrength,
                timeStacker);

            FSprite nativeFog = sLeaser.sprites[0];
            nativeFog.x = 0f;
            nativeFog.y = 0f;
            nativeFog.alpha = nativeStrength;
            nativeFog.isVisible = nativeStrength > Epsilon;

            FSprite lateFog = sLeaser.sprites[1];
            lateFog.x = 0f;
            lateFog.y = 0f;

            if (_useVolumetricComposite)
            {
                lateFog.isVisible = fogStrength > Epsilon || denseStrength > Epsilon;
                if (lateFog.isVisible)
                {
                    ApplyCompositeProperties(
                        lateFog,
                        timeStacker,
                        fogStrength,
                        denseStrength);
                }
            }
            else if (lateFog is TriangleMesh fallbackVeil)
            {
                fallbackVeil.isVisible = fallbackVeilStrength > Epsilon;
                if (fallbackVeil.isVisible)
                {
                    BuildFallbackRevealSamples(timeStacker, denseStrength);
                    UpdateFallbackVeilColors(
                        fallbackVeil,
                        camPos,
                        fallbackVeilStrength,
                        denseStrength);
                }
            }

            base.DrawSprites(sLeaser, rCam, timeStacker, camPos);
        }

        public override void ApplyPalette(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            RoomPalette palette)
        {
            base.ApplyPalette(sLeaser, rCam, palette);
            _fogColor = palette.fogColor;

            if (!_useVolumetricComposite && sLeaser.sprites.Length > 1)
            {
                sLeaser.sprites[1].color = _fogColor;
            }
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            sLeaser.sprites[0].RemoveFromContainer();
            rCam.ReturnFContainer("Foreground").AddChild(sLeaser.sprites[0]);

            sLeaser.sprites[1].RemoveFromContainer();
            rCam.ReturnFContainer("GrabShaders").AddChild(sLeaser.sprites[1]);
        }

        public void RoomUnloaded()
        {
            DisposeVolumetricResources();
            Destroy();
        }

        public override void Destroy()
        {
            DisposeVolumetricResources();
            base.Destroy();
        }

        private void DisposeVolumetricResources()
        {
            if (_volumetricResourcesDisposed)
            {
                return;
            }

            _volumetricResourcesDisposed = true;
            _fluid?.Dispose();
            _fluid = null;
            _obstacles?.Dispose();
            _obstacles = null;
        }

        private void ApplyCompositeProperties(
            FSprite sprite,
            float timeStacker,
            float fogStrength,
            float denseStrength)
        {
            Renderer renderer = sprite?._renderLayer?._meshRenderer;
            if (renderer == null)
            {
                return;
            }

            int lightCount = BuildFogLightData(timeStacker);
            int awarenessCount = BuildAwarenessData(timeStacker, denseStrength);

            Vector2 roomSize = _obstacles?.RoomSizePixels ?? new Vector2(
                Mathf.Max(1, room.TileWidth) * 20f,
                Mathf.Max(1, room.TileHeight) * 20f);

            MaterialProperties.Clear();
            renderer.GetPropertyBlock(MaterialProperties);
            MaterialProperties.SetColor(FogColorId, _fogColor);
            MaterialProperties.SetVector(RoomSizeId, new Vector4(
                roomSize.x,
                roomSize.y,
                0f,
                0f));
            MaterialProperties.SetFloat(FogIntensityId, fogStrength);
            MaterialProperties.SetFloat(DenseFogIntensityId, denseStrength);
            MaterialProperties.SetFloat(FogTimeId, _visualTime);

            MaterialProperties.SetTexture(
                FluidTextureId,
                _fluid?.DensityTexture ?? Texture2D.whiteTexture);
            MaterialProperties.SetTexture(
                ObstacleTextureId,
                _obstacles?.Texture ?? Texture2D.blackTexture);
            MaterialProperties.SetFloat(
                HasFluidId,
                _fluid?.IsAvailable == true ? 1f : 0f);

            if (DryCycleFogVolumeNoise.IsAvailable)
            {
                MaterialProperties.SetTexture(Noise3DId, DryCycleFogVolumeNoise.Texture);
                MaterialProperties.SetFloat(HasNoise3DId, 1f);
            }
            else
            {
                MaterialProperties.SetTexture(Noise3DId, null);
                MaterialProperties.SetFloat(HasNoise3DId, 0f);
            }

            MaterialProperties.SetInt(FogLightCountId, lightCount);
            MaterialProperties.SetVectorArray(FogLightsId, _fogLightData);
            MaterialProperties.SetVectorArray(FogLightColorsId, _fogLightColors);
            MaterialProperties.SetInt(AwarenessCountId, awarenessCount);
            MaterialProperties.SetVectorArray(AwarenessId, _awarenessData);
            renderer.SetPropertyBlock(MaterialProperties);
        }

        private int BuildFogLightData(float timeStacker)
        {
            Array.Clear(_fogLightData, 0, _fogLightData.Length);
            Array.Clear(_fogLightColors, 0, _fogLightColors.Length);
            int count = 0;

            for (int i = _lanterns.Count - 1;
                 i >= 0 && count < MaxFogLights;
                 i--)
            {
                Lantern lantern = _lanterns[i];
                if (lantern == null ||
                    lantern.slatedForDeletetion ||
                    lantern.room != room ||
                    lantern.firstChunk == null)
                {
                    _lanterns.RemoveAt(i);
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    lantern.firstChunk.lastPos,
                    lantern.firstChunk.pos,
                    timeStacker);
                Color color = lantern.lightSource?.color ?? new Color(1f, 0.2f, 0f);

                _fogLightData[count] = new Vector4(
                    position.x,
                    position.y,
                    LanternRevealRadius,
                    1f);
                _fogLightColors[count] = color;
                count++;
            }

            for (int i = _lanternMice.Count - 1;
                 i >= 0 && count < MaxFogLights;
                 i--)
            {
                LanternMouse mouse = _lanternMice[i];
                if (mouse == null ||
                    mouse.slatedForDeletetion ||
                    mouse.room != room ||
                    mouse.mainBodyChunk == null)
                {
                    _lanternMice.RemoveAt(i);
                    continue;
                }

                MouseGraphics graphics = mouse.graphicsModule as MouseGraphics;
                float strength = graphics == null
                    ? 0f
                    : Mathf.Clamp01(
                        graphics.LightStrength *
                        (1f - graphics.flicker * 0.4f));
                if (strength <= Epsilon)
                {
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    mouse.mainBodyChunk.lastPos,
                    mouse.mainBodyChunk.pos,
                    timeStacker);
                Color color = graphics?.lightSource?.color ?? graphics?.BodyColor ?? Color.white;

                _fogLightData[count] = new Vector4(
                    position.x,
                    position.y,
                    LanternMouseRevealRadius,
                    strength);
                _fogLightColors[count] = color;
                count++;
            }

            return count;
        }

        private int BuildAwarenessData(float timeStacker, float denseStrength)
        {
            Array.Clear(_awarenessData, 0, _awarenessData.Length);
            if (denseStrength <= Epsilon || room?.game?.Players == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0;
                 i < room.game.Players.Count && count < MaxAwarenessSources;
                 i++)
            {
                Player player = room.game.Players[i]?.realizedCreature as Player;
                if (player?.room != room ||
                    player.bodyChunks == null ||
                    player.bodyChunks.Length == 0)
                {
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    player.bodyChunks[0].lastPos,
                    player.bodyChunks[0].pos,
                    timeStacker);
                _awarenessData[count++] = new Vector4(
                    position.x,
                    position.y,
                    DenseFogPlayerAwarenessRadius,
                    DenseFogPlayerAwarenessStrength * denseStrength);
            }

            return count;
        }

        private void RefreshFogEntities()
        {
            _lanterns.Clear();
            _lanternMice.Clear();
            if (room?.updateList == null)
            {
                return;
            }

            for (int i = 0; i < room.updateList.Count; i++)
            {
                UpdatableAndDeletable obj = room.updateList[i];
                if (obj == null || obj.slatedForDeletetion || obj.room != room)
                {
                    continue;
                }

                if (obj is Lantern lantern)
                {
                    _lanterns.Add(lantern);
                }
                else if (obj is LanternMouse mouse)
                {
                    _lanternMice.Add(mouse);
                }
            }
        }

        private void BuildFallbackRevealSamples(
            float timeStacker,
            float denseStrength)
        {
            _fallbackRevealSamples.Clear();

            for (int i = _lanterns.Count - 1; i >= 0; i--)
            {
                Lantern lantern = _lanterns[i];
                if (lantern == null ||
                    lantern.slatedForDeletetion ||
                    lantern.room != room ||
                    lantern.firstChunk == null)
                {
                    _lanterns.RemoveAt(i);
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    lantern.firstChunk.lastPos,
                    lantern.firstChunk.pos,
                    timeStacker);
                _fallbackRevealSamples.Add(new RevealSample(
                    position,
                    LanternRevealRadius,
                    1f));
            }

            for (int i = _lanternMice.Count - 1; i >= 0; i--)
            {
                LanternMouse mouse = _lanternMice[i];
                if (mouse == null ||
                    mouse.slatedForDeletetion ||
                    mouse.room != room ||
                    mouse.mainBodyChunk == null)
                {
                    _lanternMice.RemoveAt(i);
                    continue;
                }

                MouseGraphics graphics = mouse.graphicsModule as MouseGraphics;
                float strength = graphics == null
                    ? 0f
                    : Mathf.Clamp01(
                        graphics.LightStrength *
                        (1f - graphics.flicker * 0.4f));
                if (strength <= Epsilon)
                {
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    mouse.mainBodyChunk.lastPos,
                    mouse.mainBodyChunk.pos,
                    timeStacker);
                _fallbackRevealSamples.Add(new RevealSample(
                    position,
                    LanternMouseRevealRadius,
                    strength));
            }

            if (denseStrength <= Epsilon || room?.game?.Players == null)
            {
                return;
            }

            for (int i = 0; i < room.game.Players.Count; i++)
            {
                Player player = room.game.Players[i]?.realizedCreature as Player;
                if (player?.room != room ||
                    player.bodyChunks == null ||
                    player.bodyChunks.Length == 0)
                {
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    player.bodyChunks[0].lastPos,
                    player.bodyChunks[0].pos,
                    timeStacker);
                _fallbackRevealSamples.Add(new RevealSample(
                    position,
                    DenseFogPlayerAwarenessRadius,
                    DenseFogPlayerAwarenessStrength * denseStrength));
            }
        }

        private void UpdateFallbackVeilColors(
            TriangleMesh veil,
            Vector2 camPos,
            float veilStrength,
            float denseStrength)
        {
            if (veil.verticeColors == null || veil.vertices == null)
            {
                return;
            }

            float linearSceneVisibility = Mathf.Clamp01(1f - veilStrength);
            float extinctSceneVisibility = Mathf.Pow(
                linearSceneVisibility,
                DenseFogFallbackContrastExponent);
            float sceneVisibility = Mathf.Lerp(
                linearSceneVisibility,
                extinctSceneVisibility,
                Mathf.Clamp01(denseStrength));
            float baseVeilAlpha = 1f - sceneVisibility;

            int count = Math.Min(veil.verticeColors.Length, veil.vertices.Length);
            for (int i = 0; i < count; i++)
            {
                Vector2 worldPosition = camPos + veil.vertices[i];
                float reveal = 0f;

                for (int j = 0; j < _fallbackRevealSamples.Count; j++)
                {
                    RevealSample sample = _fallbackRevealSamples[j];
                    float distance = Vector2.Distance(worldPosition, sample.Position);
                    if (distance >= sample.Radius)
                    {
                        continue;
                    }

                    float radial = 1f - distance / sample.Radius;
                    radial = Smooth01(radial);
                    radial = Mathf.Pow(radial, 0.58f);
                    float localReveal = Mathf.Clamp01(radial * sample.Strength);
                    reveal = 1f - (1f - reveal) * (1f - localReveal);
                    if (reveal >= 0.995f)
                    {
                        reveal = 1f;
                        break;
                    }
                }

                Color color = _fogColor;
                color.a = baseVeilAlpha * (1f - reveal);
                veil.verticeColors[i] = color;
            }

            veil.Refresh();
        }
    }

    private static ConditionalWeakTable<Room, FogWeatherController> _controllers = new();
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.Room.Loaded += Room_Loaded;
        _enabled = true;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.Room.Loaded -= Room_Loaded;
        _controllers = new ConditionalWeakTable<Room, FogWeatherController>();
        DryCycleFogVolumeNoise.Release();
        _enabled = false;
    }

    private static void Room_Loaded(On.Room.orig_Loaded orig, Room self)
    {
        orig(self);

        World world = self?.world;
        string regionId = world?.region?.name;
        if (self?.game == null ||
            world?.game == null ||
            !self.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            string.IsNullOrWhiteSpace(regionId) ||
            (!RegionClimateRegistry.RegionCanUseWeather(regionId, "Fog") &&
             !RegionClimateRegistry.RegionCanUseWeather(regionId, "DenseFog")) ||
            _controllers.TryGetValue(self, out _))
        {
            return;
        }

        FogWeatherController controller = new(self);
        _controllers.Add(self, controller);
        self.AddObject(controller);
    }

    private static bool TryEvaluate(Room room, out float fog, out float denseFog)
    {
        fog = 0f;
        denseFog = 0f;

        World world = room?.world;
        if (world?.game == null ||
            !world.game.IsStorySession ||
            !RegionDayNightOptions.IsEnabled(world) ||
            !WorldClockHooks.TryGetClock(world, out WorldClock clock))
        {
            return false;
        }

        WeatherScheduleRuntime.Synchronize(world);
        fog = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "Fog");
        denseFog = WeatherScheduleRuntime.GetIntensity(
            world,
            clock,
            WeatherScheduleEventKind.Weather,
            "DenseFog");
        return fog > Epsilon || denseFog > Epsilon;
    }

    private static TriangleMesh CreateFallbackVeilMesh(float width, float height)
    {
        TriangleMesh.Triangle[] triangles =
            new TriangleMesh.Triangle[FallbackVeilColumns * FallbackVeilRows * 2];

        int triangleIndex = 0;
        for (int y = 0; y < FallbackVeilRows; y++)
        {
            for (int x = 0; x < FallbackVeilColumns; x++)
            {
                int rowWidth = FallbackVeilColumns + 1;
                int bottomLeft = y * rowWidth + x;
                int bottomRight = bottomLeft + 1;
                int topLeft = (y + 1) * rowWidth + x;
                int topRight = topLeft + 1;

                triangles[triangleIndex++] = new TriangleMesh.Triangle(
                    bottomLeft,
                    bottomRight,
                    topLeft);
                triangles[triangleIndex++] = new TriangleMesh.Triangle(
                    bottomRight,
                    topRight,
                    topLeft);
            }
        }

        TriangleMesh mesh = new("Futile_White", triangles, customColor: true);
        for (int y = 0; y <= FallbackVeilRows; y++)
        {
            float py = height * y / FallbackVeilRows;
            for (int x = 0; x <= FallbackVeilColumns; x++)
            {
                float px = width * x / FallbackVeilColumns;
                int index = y * (FallbackVeilColumns + 1) + x;
                mesh.vertices[index] = new Vector2(px, py);
            }
        }
        mesh.Refresh();
        return mesh;
    }

    private static float Smooth01(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3f - 2f * t);
    }
}
