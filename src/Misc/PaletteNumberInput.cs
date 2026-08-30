using System;
using System.Collections.Generic;
using System.Globalization;
using DevInterface;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Adds direct integer keyboard entry to all vanilla Room Settings palette number
/// controls without replacing PaletteController itself. The stock arrows, inherited
/// values and camera refresh behavior remain intact.
/// </summary>
internal static class PaletteNumberInput
{
    private const string OverlayIdPrefix = "DryCycle_Palette_Number_Input_";
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.DevInterface.RoomSettingsPage.ctor += RoomSettingsPage_ctor;
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
        DevUI owner,
        string IDstring,
        DevUINode parentNode,
        string name)
    {
        orig(self, owner, IDstring, parentNode, name);

        List<PaletteController> controllers = new();
        CollectPaletteControllers(self, controllers);
        for (int i = 0; i < controllers.Count; i++)
        {
            AddInputOverlay(owner, controllers[i]);
        }
    }

    private static void CollectPaletteControllers(DevUINode node, List<PaletteController> result)
    {
        if (node == null)
        {
            return;
        }

        if (node is PaletteController paletteController)
        {
            result.Add(paletteController);
        }

        if (node.subNodes == null)
        {
            return;
        }

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            CollectPaletteControllers(node.subNodes[i], result);
        }
    }

    private static void AddInputOverlay(DevUI owner, PaletteController controller)
    {
        if (controller == null || controller.controlPoint < 0 || controller.controlPoint > 3)
        {
            return;
        }

        string overlayId = OverlayIdPrefix + controller.IDstring;
        for (int i = 0; i < controller.subNodes.Count; i++)
        {
            if (controller.subNodes[i]?.IDstring == overlayId)
            {
                return;
            }
        }

        // IntegerControl's stock number label is at x=140, width=36. Overlay only
        // that label; the stock Less/More arrows remain untouched.
        controller.subNodes.Add(new PaletteNumberInputButton(
            owner,
            overlayId,
            controller,
            new Vector2(140f, 0f),
            36f,
            controller));
    }

    private sealed class PaletteNumberInputButton : Button
    {
        private const int MaxDigits = 10;

        private readonly PaletteController _controller;
        private bool _editing;
        private bool _replaceOnFirstEditKey;
        private string _buffer = string.Empty;

        public PaletteNumberInputButton(
            DevUI owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            float width,
            PaletteController controller)
            : base(owner, IDstring, parentNode, pos, width, "")
        {
            _controller = controller;
            SyncFromVanillaLabel();
            ApplyVanillaColors();
        }

        public override void Clicked()
        {
            if (!_editing)
            {
                BeginEdit();
            }
        }

        public override void Update()
        {
            base.Update();

            // Button normally uses red text and a red/blue hover state. This overlay
            // is intended to look exactly like the vanilla IntegerControl number box,
            // so force the original black-text / pale-background appearance.
            ApplyVanillaColors();

            if (!_editing)
            {
                SyncFromVanillaLabel();
                return;
            }

            if (owner != null && owner.mouseClick && !MouseOver)
            {
                Commit();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cancel();
                return;
            }

            bool commitRequested = false;
            string input = Input.inputString;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c >= '0' && c <= '9')
                {
                    if (_replaceOnFirstEditKey)
                    {
                        _buffer = string.Empty;
                        _replaceOnFirstEditKey = false;
                    }

                    if (_buffer.Length < MaxDigits)
                    {
                        _buffer += c;
                    }
                }
                else if (c == '\b')
                {
                    if (_replaceOnFirstEditKey)
                    {
                        _buffer = string.Empty;
                        _replaceOnFirstEditKey = false;
                    }
                    else if (_buffer.Length > 0)
                    {
                        _buffer = _buffer.Substring(0, _buffer.Length - 1);
                    }
                }
                else if (c == '\n' || c == '\r')
                {
                    commitRequested = true;
                }
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                commitRequested = true;
            }

            if (commitRequested)
            {
                Commit();
                return;
            }

            SetDisplayedText((_buffer.Length == 0 ? string.Empty : _buffer) + "_");
        }

        private void BeginEdit()
        {
            RoomSettings roomSettings = _controller?.RoomSettings;
            if (roomSettings == null)
            {
                return;
            }

            _buffer = CurrentValueText(roomSettings);
            _replaceOnFirstEditKey = true;
            _editing = true;
            SetDisplayedText(_buffer + "_");
        }

        private string CurrentValueText(RoomSettings roomSettings)
        {
            return _controller.controlPoint switch
            {
                0 => Math.Max(0, roomSettings.Palette).ToString(CultureInfo.InvariantCulture),
                1 => Math.Max(0, roomSettings.EffectColorA).ToString(CultureInfo.InvariantCulture),
                2 => Math.Max(0, roomSettings.EffectColorB).ToString(CultureInfo.InvariantCulture),
                3 => roomSettings.fadePalette == null
                    ? string.Empty
                    : Math.Max(0, roomSettings.fadePalette.palette).ToString(CultureInfo.InvariantCulture),
                _ => string.Empty
            };
        }

        private void Commit()
        {
            if (!_editing)
            {
                return;
            }

            _editing = false;
            _replaceOnFirstEditKey = false;

            if (_controller?.RoomSettings == null || string.IsNullOrEmpty(_buffer))
            {
                _controller?.Refresh();
                SyncFromVanillaLabel();
                return;
            }

            if (!long.TryParse(_buffer, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                _controller.Refresh();
                SyncFromVanillaLabel();
                return;
            }

            int value = (int)Math.Min(int.MaxValue, Math.Max(0L, parsed));
            ApplyValue(value);
            _controller.Refresh();
            SyncFromVanillaLabel();
        }

        private void ApplyValue(int value)
        {
            RoomSettings roomSettings = _controller.RoomSettings;
            RainWorldGame game = owner?.room?.game;
            RoomCamera camera = game?.cameras != null && game.cameras.Length > 0
                ? game.cameras[0]
                : null;

            switch (_controller.controlPoint)
            {
                case 0:
                    roomSettings.pal = value;
                    camera?.ChangeMainPalette(roomSettings.Palette);
                    break;

                case 1:
                    roomSettings.eColA = value;
                    camera?.ApplyEffectColorsToAllPaletteTextures(
                        roomSettings.EffectColorA,
                        roomSettings.EffectColorB);
                    break;

                case 2:
                    roomSettings.eColB = value;
                    camera?.ApplyEffectColorsToAllPaletteTextures(
                        roomSettings.EffectColorA,
                        roomSettings.EffectColorB);
                    break;

                case 3:
                    if (roomSettings.fadePalette == null)
                    {
                        int screenCount = owner?.room?.cameraPositions?.Length ?? 1;
                        roomSettings.fadePalette = new RoomSettings.FadePalette(value, Math.Max(1, screenCount));
                    }
                    else
                    {
                        roomSettings.fadePalette.palette = value;
                    }

                    if (camera != null)
                    {
                        float fade = 0f;
                        int cameraIndex = camera.currentCameraPosition;
                        if (roomSettings.fadePalette.fades != null
                            && cameraIndex >= 0
                            && cameraIndex < roomSettings.fadePalette.fades.Length)
                        {
                            fade = roomSettings.fadePalette.fades[cameraIndex];
                        }

                        camera.ChangeFadePalette(value, fade);
                    }

                    _controller.parentNode?.Refresh();
                    break;
            }
        }

        private void Cancel()
        {
            _editing = false;
            _replaceOnFirstEditKey = false;
            _controller?.Refresh();
            SyncFromVanillaLabel();
        }

        private void SyncFromVanillaLabel()
        {
            if (_controller == null)
            {
                return;
            }

            SetDisplayedText(_controller.NumberLabelText);
            ApplyVanillaColors();
        }

        private void ApplyVanillaColors()
        {
            textColor = Color.black;
            spriteColor = Color.white;
            if (fSprites != null && fSprites.Count > 0)
            {
                fSprites[0].alpha = 0.5f;
            }
        }

        private void SetDisplayedText(string text)
        {
            if (fLabels != null && fLabels.Count > 0)
            {
                fLabels[0].text = text ?? string.Empty;
            }
        }
    }
}
