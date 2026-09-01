using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DryCycle.DayNight;
using DryCycle.Weather.Climate;
using DryCycle.Weather.Scheduling;
using UnityEngine;

namespace DryCycle.Weather;

/// <summary>
/// WorldClock-driven Fog / DenseFog renderer.
///
/// Rain World's native Fog shader supplies the atmospheric color/desaturation pass.
/// A separate palette-colored screen veil owns the short visibility range that a
/// native Fog pass cannot reach on its own. DenseFog additionally applies a nonlinear
/// visibility extinction curve so high-contrast thin details such as poles and chains
/// lose almost all scene contribution instead of merely showing through a gray alpha
/// overlay.
///
/// Fog reveal is deliberately restricted to Lantern and LanternMouse. Their reveal
/// radii are fixed at the minimum designed radius of each object instead of following
/// the continuously changing LightSource.Rad, preventing the edge of a clear area from
/// pulsing or twitching with native flicker/breath animation. LanternMouse reveal
/// strength may still change with its battery/light state; only the radius is fixed.
///
/// Mapper-authored RoomSettings Fog is never replaced or clamped against this weather:
/// the authored camera effect renders normally and DryCycle layers on top of it.
/// </summary>
internal static class FogWeatherRuntime
{
    private const float Epsilon = 0.0001f;

    // Native Fog provides the atmospheric look. The veil owns actual visibility loss.
    private const float FogNativeStrength = 0.92f;
    private const float DenseFogNativeStrength = 1.00f;

    // Fog remains navigable without a lamp. DenseFog intentionally approaches an
    // opaque Fog-Gulch-like wall outside nearby/light-revealed areas.
    private const float FogVeilStrength = 0.48f;
    private const float DenseFogVeilStrength = 0.94f;

    // At full DenseFog, the old 0.94 alpha veil still left 6% of the original scene,
    // enough for black poles/chains to remain readable. Extinction applies an exponent
    // to that remaining scene visibility: 0.06^2.2 ~= 0.002, leaving only ~0.2% of the
    // original contrast outside reveal areas while ordinary Fog keeps its old response.
    private const float DenseFogContrastExponent = 2.20f;

    // DenseFog keeps a tiny minimum player-local visibility bubble so movement remains
    // possible without a lamp. This is awareness, not a fog-penetrating light source.
    private const float DenseFogPlayerAwarenessRadius = 58f;
    private const float DenseFogPlayerAwarenessStrength = 0.84f;

    // Fixed clear radii. Lantern's native 250*flicker targets roughly 0.8..1.2, giving
    // a designed minimum of 200 px. LanternMouse's native radius can contract to 40 px
    // while charging; use that minimum rather than its breathing/flickering Rad.
    private const float LanternRevealRadius = 200f;
    private const float LanternMouseRevealRadius = 40f;

    // ~34 px cells at 1366x768. Vertex interpolation keeps circular reveals smooth
    // while staying well below the cost of a full-screen per-pixel CPU mask.
    private const int VeilColumns = 40;
    private const int VeilRows = 23;
    private const int LightRefreshTicks = 10;

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

    private sealed class FogWeatherController : CosmeticSprite
    {
        private readonly List<Lantern> _lanterns = new();
        private readonly List<LanternMouse> _lanternMice = new();
        private readonly List<RevealSample> _revealSamples = new();

        private float _lastNativeStrength;
        private float _nativeStrength;
        private float _lastVeilStrength;
        private float _veilStrength;
        private float _lastDenseStrength;
        private float _denseStrength;
        private int _lightRefreshCounter;
        private Color _fogColor = new(0.7f, 0.72f, 0.72f);

        internal FogWeatherController(Room room)
        {
            this.room = room;
        }

        public override void Update(bool eu)
        {
            base.Update(eu);

            _lastNativeStrength = _nativeStrength;
            _lastVeilStrength = _veilStrength;
            _lastDenseStrength = _denseStrength;

            if (!TryEvaluate(room, out float fog, out float denseFog))
            {
                _nativeStrength = 0f;
                _veilStrength = 0f;
                _denseStrength = 0f;
                return;
            }

            _nativeStrength = Mathf.Clamp01(
                fog * FogNativeStrength + denseFog * DenseFogNativeStrength);
            _veilStrength = Mathf.Clamp01(
                fog * FogVeilStrength + denseFog * DenseFogVeilStrength);
            _denseStrength = Mathf.Clamp01(denseFog);

            if (--_lightRefreshCounter <= 0)
            {
                RefreshFogLights();
                _lightRefreshCounter = LightRefreshTicks;
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

            TriangleMesh veil = CreateVeilMesh(screenWidth, screenHeight);
            veil.shader = rCam.game.rainWorld.Shaders["Basic"];
            veil.isVisible = false;

            sLeaser.sprites = new FSprite[] { nativeFog, veil };
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

            float nativeStrength = Mathf.Lerp(
                _lastNativeStrength,
                _nativeStrength,
                timeStacker);
            float veilStrength = Mathf.Lerp(
                _lastVeilStrength,
                _veilStrength,
                timeStacker);
            float denseStrength = Mathf.Lerp(
                _lastDenseStrength,
                _denseStrength,
                timeStacker);

            FSprite nativeFog = sLeaser.sprites[0];
            nativeFog.x = 0f;
            nativeFog.y = 0f;
            nativeFog.alpha = nativeStrength;
            nativeFog.isVisible = nativeStrength > Epsilon;

            TriangleMesh veil = sLeaser.sprites[1] as TriangleMesh;
            if (veil != null)
            {
                veil.x = 0f;
                veil.y = 0f;
                veil.isVisible = veilStrength > Epsilon;
                if (veil.isVisible)
                {
                    BuildRevealSamples(timeStacker, denseStrength);
                    UpdateVeilColors(veil, camPos, veilStrength, denseStrength);
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
        }

        public override void AddToContainer(
            RoomCamera.SpriteLeaser sLeaser,
            RoomCamera rCam,
            FContainer newContatiner)
        {
            // The native atmospheric Fog pass belongs with the normal foreground
            // full-screen effects. The dense veil is intentionally late: transparent
            // holes reveal the already-rendered level and the two permitted fog lights.
            sLeaser.sprites[0].RemoveFromContainer();
            rCam.ReturnFContainer("Foreground").AddChild(sLeaser.sprites[0]);

            sLeaser.sprites[1].RemoveFromContainer();
            rCam.ReturnFContainer("GrabShaders").AddChild(sLeaser.sprites[1]);
        }

        private void RefreshFogLights()
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

        private void BuildRevealSamples(float timeStacker, float denseStrength)
        {
            _revealSamples.Clear();

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
                _revealSamples.Add(new RevealSample(
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
                _revealSamples.Add(new RevealSample(
                    position,
                    LanternMouseRevealRadius,
                    strength));
            }

            if (denseStrength <= Epsilon || room?.game?.Players == null)
            {
                return;
            }

            // Baseline close-range awareness is intentionally tiny and only scales in
            // with DenseFog. It is separate from the Lantern/LanternMouse light list.
            for (int i = 0; i < room.game.Players.Count; i++)
            {
                Player player = room.game.Players[i]?.realizedCreature as Player;
                if (player?.room != room || player.bodyChunks == null || player.bodyChunks.Length == 0)
                {
                    continue;
                }

                Vector2 position = Vector2.Lerp(
                    player.bodyChunks[0].lastPos,
                    player.bodyChunks[0].pos,
                    timeStacker);
                _revealSamples.Add(new RevealSample(
                    position,
                    DenseFogPlayerAwarenessRadius,
                    DenseFogPlayerAwarenessStrength * denseStrength));
            }
        }

        private void UpdateVeilColors(
            TriangleMesh veil,
            Vector2 camPos,
            float veilStrength,
            float denseStrength)
        {
            if (veil.verticeColors == null || veil.vertices == null)
            {
                return;
            }

            // Convert the old linear veil alpha into a scene-visibility value, then
            // collapse that visibility nonlinearly as DenseFog approaches full strength.
            // Ordinary Fog (denseStrength=0) is unchanged.
            float linearSceneVisibility = Mathf.Clamp01(1f - veilStrength);
            float extinctSceneVisibility = Mathf.Pow(
                linearSceneVisibility,
                DenseFogContrastExponent);
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

                for (int j = 0; j < _revealSamples.Count; j++)
                {
                    RevealSample sample = _revealSamples[j];
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

    private static TriangleMesh CreateVeilMesh(float width, float height)
    {
        TriangleMesh.Triangle[] triangles =
            new TriangleMesh.Triangle[VeilColumns * VeilRows * 2];

        int triangleIndex = 0;
        for (int y = 0; y < VeilRows; y++)
        {
            for (int x = 0; x < VeilColumns; x++)
            {
                int rowWidth = VeilColumns + 1;
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
        for (int y = 0; y <= VeilRows; y++)
        {
            float py = height * y / VeilRows;
            for (int x = 0; x <= VeilColumns; x++)
            {
                float px = width * x / VeilColumns;
                int index = y * (VeilColumns + 1) + x;
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
