using System;
using System.Collections.Generic;
using System.Globalization;
using DevInterface;
using DryCycle.DevUI.Controls;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.Misc;

/// <summary>
/// Adds direct keyboard integer entry to Rain World's vanilla PaletteController rows.
///
/// Vanilla palette controls are intentionally handled with a Button overlay rather than
/// replacing IntegerControl.subNodes[1]. The overlay path is the interaction model that
/// works reliably with Rain World's DevUI click dispatch while preserving the stock
/// title, arrows, inheritance markers (&lt;A&gt;/&lt;T&gt;) and NONE presentation.
///
/// DryCycle-owned IntegerControls (for example Dusk/Night) continue to use the reusable
/// DryCycleIntegerField through IntegerControlInputBinding.
/// </summary>
internal static class PaletteDirectInputRuntime
{
    private const string OverlayIdPrefix = "DryCycle_Palette_Number_Input_";
    private static bool _enabled;
    private static PaletteIntegerInputButton _activeInput;

    /// <summary>
    /// True while one vanilla palette value owns keyboard input. DevUIShortcutInputGuard
    /// uses this exactly like DryCycleInputFocus so digit editing cannot leak into player
    /// controls or raw DevTools shortcuts.
    /// </summary>
    internal static bool HasActiveInput => _activeInput != null;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        On.DevInterface.RoomSettingsPage.ctor += RoomSettingsPage_ctor;
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        On.DevInterface.RoomSettingsPage.ctor -= RoomSettingsPage_ctor;
        _activeInput?.CommitFromRuntime();
        _activeInput = null;
        DryCycleInputFocus.Reset(commit: true);
        _enabled = false;
    }

    private static void RoomSettingsPage_ctor(
        On.DevInterface.RoomSettingsPage.orig_ctor orig,
        RoomSettingsPage self,
        DevUIOwner owner,
        string IDstring,
        DevUINode parentNode,
        string name)
    {
        orig(self, owner, IDstring, parentNode, name);

        List<PaletteController> controllers = new();
        CollectPaletteControllers(self, controllers);
        for (int i = 0; i < controllers.Count; i++)
        {
            AttachPaletteController(owner, controllers[i]);
        }
    }

    /// <summary>
    /// Attaches the proven vanilla-DevUI Button overlay. The historical return type is
    /// retained so existing DryCycle call sites remain source-compatible; vanilla palette
    /// controls no longer use a DryCycleIntegerField and therefore return null here.
    /// </summary>
    internal static DryCycleIntegerField AttachPaletteController(DevUIOwner owner, PaletteController controller)
    {
        if (owner == null || controller == null || controller.controlPoint < 0 || controller.controlPoint > 3)
        {
            return null;
        }

        string overlayId = OverlayIdPrefix + controller.IDstring;
        if (controller.subNodes != null)
        {
            for (int i = 0; i < controller.subNodes.Count; i++)
            {
                if (controller.subNodes[i]?.IDstring == overlayId)
                {
                    return null;
                }
            }
        }

        // IntegerControl's stock Number label occupies x=140..176. Overlay exactly that
        // rectangle; Less/More remain the original ArrowButtons at x=120 and x=180.
        controller.subNodes.Add(new PaletteIntegerInputButton(
            owner,
            overlayId,
            controller,
            new Vector2(140f, 0f),
            36f,
            controller,
            () => CurrentPaletteValueText(controller),
            value => ApplyPaletteValue(owner, controller, value)));

        return null;
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

    private static string CurrentPaletteValueText(PaletteController controller)
    {
        RoomSettings settings = controller?.RoomSettings;
        if (settings == null)
        {
            return string.Empty;
        }

        return controller.controlPoint switch
        {
            0 => Math.Max(0, settings.Palette).ToString(CultureInfo.InvariantCulture),
            1 => Math.Max(0, settings.EffectColorA).ToString(CultureInfo.InvariantCulture),
            2 => Math.Max(0, settings.EffectColorB).ToString(CultureInfo.InvariantCulture),
            3 => settings.fadePalette == null
                ? string.Empty
                : Math.Max(0, settings.fadePalette.palette).ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private static void ApplyPaletteValue(DevUIOwner owner, PaletteController controller, int value)
    {
        RoomSettings settings = controller?.RoomSettings;
        if (settings == null)
        {
            return;
        }

        RoomCamera camera = owner?.room?.game?.cameras != null && owner.room.game.cameras.Length > 0
            ? owner.room.game.cameras[0]
            : null;

        switch (controller.controlPoint)
        {
            case 0:
                settings.pal = value;
                camera?.ChangeMainPalette(settings.Palette);
                break;

            case 1:
                settings.eColA = value;
                camera?.ApplyEffectColorsToAllPaletteTextures(settings.EffectColorA, settings.EffectColorB);
                break;

            case 2:
                settings.eColB = value;
                camera?.ApplyEffectColorsToAllPaletteTextures(settings.EffectColorA, settings.EffectColorB);
                break;

            case 3:
                if (settings.fadePalette == null)
                {
                    int screenCount = owner?.room?.cameraPositions?.Length ?? 1;
                    settings.fadePalette = new RoomSettings.FadePalette(value, Math.Max(1, screenCount));
                }
                else
                {
                    settings.fadePalette.palette = value;
                }

                if (camera != null)
                {
                    int cameraIndex = camera.currentCameraPosition;
                    float fade = settings.fadePalette.fades != null
                        && cameraIndex >= 0
                        && cameraIndex < settings.fadePalette.fades.Length
                        ? settings.fadePalette.fades[cameraIndex]
                        : 0f;
                    camera.ChangeFadePalette(value, fade);
                }

                controller.parentNode?.Refresh();
                break;
        }
    }

    private static void ClaimInput(PaletteIntegerInputButton input)
    {
        if (input == null)
        {
            return;
        }

        if (_activeInput != null && !ReferenceEquals(_activeInput, input))
        {
            _activeInput.CommitFromRuntime();
        }

        // Only one DryCycle text-input owner should exist at a time. Committing a generic
        // field here also prevents its keyboard guard from remaining latched behind the
        // vanilla palette overlay.
        DryCycleInputFocus.Reset(commit: true);
        _activeInput = input;
    }

    private static void ReleaseInput(PaletteIntegerInputButton input)
    {
        if (ReferenceEquals(_activeInput, input))
        {
            _activeInput = null;
        }
    }

    /// <summary>
    /// Clickable overlay for the stock Number label. This is deliberately close to the
    /// pre-framework PaletteNumberInput interaction that was already validated in game.
    /// </summary>
    private sealed class PaletteIntegerInputButton : Button
    {
        private const int MaxDigits = 10;

        private readonly PaletteController _controller;
        private readonly Func<string> _currentValueText;
        private readonly Action<int> _applyValue;
        private bool _editing;
        private bool _replaceOnFirstEditKey;
        private string _buffer = string.Empty;

        internal PaletteIntegerInputButton(
            DevUIOwner owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            float width,
            PaletteController controller,
            Func<string> currentValueText,
            Action<int> applyValue)
            : base(owner, IDstring, parentNode, pos, width, string.Empty)
        {
            _controller = controller;
            _currentValueText = currentValueText;
            _applyValue = applyValue;
            SyncFromController();
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
            ApplyVanillaColors();

            if (!_editing)
            {
                SyncFromController();
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

        public override void ClearSprites()
        {
            if (_editing)
            {
                Commit();
            }
            else
            {
                ReleaseInput(this);
            }

            base.ClearSprites();
        }

        internal void CommitFromRuntime()
        {
            if (_editing)
            {
                Commit();
            }
            else
            {
                ReleaseInput(this);
            }
        }

        private void BeginEdit()
        {
            ClaimInput(this);
            _buffer = _currentValueText?.Invoke() ?? string.Empty;
            _replaceOnFirstEditKey = true;
            _editing = true;
            SetDisplayedText(_buffer + "_");
        }

        private void Commit()
        {
            if (!_editing)
            {
                ReleaseInput(this);
                return;
            }

            _editing = false;
            _replaceOnFirstEditKey = false;

            if (!string.IsNullOrEmpty(_buffer)
                && long.TryParse(_buffer, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                int value = (int)Math.Min(int.MaxValue, Math.Max(0L, parsed));
                _applyValue?.Invoke(value);
            }

            _controller?.Refresh();
            SyncFromController();
            ReleaseInput(this);
        }

        private void Cancel()
        {
            _editing = false;
            _replaceOnFirstEditKey = false;
            _controller?.Refresh();
            SyncFromController();
            ReleaseInput(this);
        }

        private void SyncFromController()
        {
            if (_controller == null)
            {
                return;
            }

            // Keep Rain World's own inherited presentation when not editing.
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
