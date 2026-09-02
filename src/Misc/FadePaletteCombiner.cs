using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using DevInterface;
using DryCycle.DevUI.Controls;
using RWCustom;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Dev-tools utility that bakes the currently displayed room fade into a standalone
/// 32x16 Rain World palette PNG. It has no RegionKit dependency; when RegionKit's
/// MoreFadePalettes module is present, its additional fades are included as well.
/// </summary>
internal static class FadePaletteCombiner
{
    private const string PanelId = "DryCycle_Fade_Palette_Combiner_Panel";
    private const string SaveButtonId = "DryCycle_Save_Combined_Palette";

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        On.DevInterface.RoomSettingsPage.ctor += RoomSettingsPage_ctor;
        _enabled = true;
    }

    public static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.RoomSettingsPage.ctor -= RoomSettingsPage_ctor;
        _enabled = false;
    }

    private static void RoomSettingsPage_ctor(
        On.DevInterface.RoomSettingsPage.orig_ctor orig,
        RoomSettingsPage self,
        DevInterface.DevUI owner,
        string IDstring,
        DevUINode parentNode,
        string name)
    {
        orig(self, owner, IDstring, parentNode, name);

        // Keep this next to DryCycle's DAY / NIGHT PALETTES panel rather than
        // overlapping it at RegionKit's original (260, 190) position.
        FadePaletteCombinerPanel panel = new(
            owner,
            PanelId,
            self,
            new Vector2(510f, 190f),
            new Vector2(230f, 85f),
            "FADE PALETTE COMBINER");

        self.subNodes.Add(panel);
    }

    private sealed class FadePaletteCombinerPanel : Panel, IDevUISignals
    {
        private readonly OutputPaletteController _paletteController;
        private readonly DevUILabel _statusLabel;

        public FadePaletteCombinerPanel(
            DevInterface.DevUI owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            Vector2 size,
            string title)
            : base(owner, IDstring, parentNode, pos, size, title)
        {
            _paletteController = new OutputPaletteController(
                owner,
                "DryCycle_New_Combined_Palette",
                this,
                new Vector2(5f, size.y - 20f),
                "New Palette:");
            subNodes.Add(_paletteController);

            IntegerControlInputBinding.Attach(
                owner,
                _paletteController,
                () => _paletteController.NewPaletteNumber,
                value =>
                {
                    _paletteController.NewPaletteNumber = Math.Max(0, value);
                    _paletteController.Refresh();
                },
                minValue: 0,
                maxValue: int.MaxValue);

            subNodes.Add(new Button(
                owner,
                SaveButtonId,
                this,
                new Vector2(5f, size.y - 60f),
                220f,
                "Save fade as new palette"));

            _statusLabel = new DevUILabel(
                owner,
                "DryCycle_Combined_Palette_Output_Label",
                this,
                new Vector2(5f, size.y - 80f),
                220f,
                "Output: palettes/combinedPalettes");
            subNodes.Add(_statusLabel);
        }

        public void Signal(DevUISignalType type, DevUINode sender, string message)
        {
            if (sender?.IDstring != SaveButtonId)
            {
                return;
            }

            TrySaveCombinedPalette();
        }

        private void TrySaveCombinedPalette()
        {
            RainWorldGame game = owner?.room?.game;
            RoomCamera roomCamera = game?.cameras != null && game.cameras.Length > 0
                ? game.cameras[0]
                : null;

            if (roomCamera?.fadeTexA == null || roomCamera.fadeTexB == null)
            {
                SetStatus("No active fade palette");
                return;
            }

            Texture2D newPaletteTexture = null;

            try
            {
                string directory = Path.Combine(
                    Custom.RootFolderDirectory(),
                    "palettes",
                    "combinedPalettes");
                Directory.CreateDirectory(directory);

                newPaletteTexture = new Texture2D(32, 16);

                // Bake the exact two vanilla fade textures currently used by the
                // room camera. fadeCoord.x is the room's current fade amount.
                for (int x = 0; x < 32; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        newPaletteTexture.SetPixel(
                            x,
                            y,
                            Color.Lerp(
                                roomCamera.fadeTexA.GetPixel(x, y),
                                roomCamera.fadeTexB.GetPixel(x, y),
                                roomCamera.fadeCoord.x));
                    }
                }

                // Optional compatibility: reproduce RegionKit's combiner behavior
                // when its MoreFadePalettes module is present, without referencing
                // RegionKit at compile time.
                ApplyRegionKitMoreFadesIfAvailable(roomCamera, newPaletteTexture);

                // Effect colors are applied to palette textures at runtime. They must
                // not be baked into the authored palette PNG, matching RegionKit's
                // original FadePaletteCombiner behavior.
                Color[] clearEffectColors =
                {
                    Color.white, Color.white, Color.white, Color.white,
                    Color.white, Color.white, Color.white, Color.white
                };
                newPaletteTexture.SetPixels(30, 2, 2, 4, clearEffectColors);
                newPaletteTexture.SetPixels(30, 10, 2, 4, clearEffectColors);
                newPaletteTexture.Apply(false, false);

                string outputPath = Path.Combine(
                    directory,
                    $"palette{_paletteController.NewPaletteNumber}.png");

                PNGSaver.SaveTextureToFile(newPaletteTexture, outputPath);
                SetStatus($"Saved palette{_paletteController.NewPaletteNumber}.png");
                Plugin.Logger?.LogInfo($"Fade palette combined: {outputPath}");
            }
            catch (Exception ex)
            {
                SetStatus("Save failed - check log");
                Plugin.Logger?.LogError($"Failed to combine fade palette: {ex}");
            }
            finally
            {
                if (newPaletteTexture != null)
                {
                    UnityEngine.Object.Destroy(newPaletteTexture);
                }
            }
        }

        private void SetStatus(string text)
        {
            if (_statusLabel != null)
            {
                _statusLabel.Text = text ?? string.Empty;
            }
        }
    }

    private static void ApplyRegionKitMoreFadesIfAvailable(
        RoomCamera roomCamera,
        Texture2D outputTexture)
    {
        try
        {
            Type moreFadeType = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length && moreFadeType == null; i++)
            {
                moreFadeType = assemblies[i].GetType(
                    "RegionKit.Modules.Misc.MoreFadePalettes",
                    false);
            }

            if (moreFadeType == null)
            {
                return;
            }

            MethodInfo moreFadeTexturesMethod = moreFadeType.GetMethod(
                "MoreFadeTextures",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (moreFadeTexturesMethod == null)
            {
                return;
            }

            object dictionaryObject = moreFadeTexturesMethod.Invoke(null, new object[] { roomCamera });
            if (dictionaryObject is not IDictionary dictionary || dictionary.Count == 0)
            {
                return;
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not RoomSettings.FadePalette fade
                    || entry.Value is not Texture2D fadeTexture)
                {
                    continue;
                }

                int cameraIndex = roomCamera.currentCameraPosition;
                if (fade.fades == null
                    || cameraIndex < 0
                    || cameraIndex >= fade.fades.Length)
                {
                    continue;
                }

                float amount = fade.fades[cameraIndex];
                for (int x = 0; x < 32; x++)
                {
                    for (int y = 0; y < 16; y++)
                    {
                        outputTexture.SetPixel(
                            x,
                            y,
                            Color.Lerp(
                                outputTexture.GetPixel(x, y),
                                fadeTexture.GetPixel(x, y),
                                amount));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // RegionKit integration is optional. A RegionKit API/version change must
            // never break DryCycle's vanilla two-palette combiner.
            Plugin.Logger?.LogWarning($"Could not include RegionKit MoreFadePalettes: {ex.Message}");
        }
    }

    private sealed class OutputPaletteController : IntegerControl
    {
        public int NewPaletteNumber { get; set; }

        public OutputPaletteController(
            DevInterface.DevUI owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            string title)
            : base(owner, IDstring, parentNode, pos, title)
        {
            NewPaletteNumber = 0;
            Refresh();
        }

        public override void Increment(int change)
        {
            NewPaletteNumber = Math.Max(0, NewPaletteNumber + change);
            Refresh();
        }

        public override void Refresh()
        {
            NumberLabelText = NewPaletteNumber.ToString(CultureInfo.InvariantCulture);
            base.Refresh();
        }
    }
}
