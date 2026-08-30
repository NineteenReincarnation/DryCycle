using System;
using System.Globalization;
using DevInterface;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Adds direct integer keyboard entry to the vanilla Room Settings "Palette" number
/// field without replacing PaletteController itself. The original arrows, inherited
/// palette semantics and camera refresh behavior remain intact.
/// </summary>
internal static class PaletteNumberInput
{
    private const string OverlayId = "DryCycle_Palette_Number_Input";
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

        PaletteController paletteController = FindBasePaletteController(self);
        if (paletteController == null)
        {
            return;
        }

        for (int i = 0; i < paletteController.subNodes.Count; i++)
        {
            if (paletteController.subNodes[i]?.IDstring == OverlayId)
            {
                return;
            }
        }

        // IntegerControl's stock number label is at x=140, width=36. Put the editable
        // button directly over it. Because this node is appended last, it updates
        // before the stock Less/More arrows; clicking an arrow while editing therefore
        // commits the typed value first, then vanilla applies the arrow increment.
        paletteController.subNodes.Add(new PaletteNumberInputButton(
            owner,
            OverlayId,
            paletteController,
            new Vector2(140f, 0f),
            36f,
            paletteController));
    }

    private static PaletteController FindBasePaletteController(DevUINode node)
    {
        if (node is PaletteController paletteController && paletteController.controlPoint == 0)
        {
            return paletteController;
        }

        if (node?.subNodes == null)
        {
            return null;
        }

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            PaletteController found = FindBasePaletteController(node.subNodes[i]);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class PaletteNumberInputButton : Button
    {
        private const int MaxDigits = 10;

        private readonly PaletteController _controller;
        private bool _editing;
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

            if (!_editing)
            {
                SyncFromVanillaLabel();
                return;
            }

            // Since this overlay updates before the vanilla arrows, an outside click
            // commits first. The clicked vanilla control can then act on the new value
            // during the same DevUI update pass.
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
                    if (_buffer.Length < MaxDigits)
                    {
                        _buffer += c;
                    }
                }
                else if (c == '\b')
                {
                    if (_buffer.Length > 0)
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

            // A small caret makes it explicit that the otherwise vanilla-looking
            // number box currently owns keyboard input.
            SetDisplayedText((_buffer.Length == 0 ? "" : _buffer) + "_");
        }

        private void BeginEdit()
        {
            RoomSettings roomSettings = _controller?.RoomSettings;
            if (roomSettings == null)
            {
                return;
            }

            _buffer = Math.Max(0, roomSettings.Palette).ToString(CultureInfo.InvariantCulture);
            _editing = true;
            SetDisplayedText(_buffer + "_");
        }

        private void Commit()
        {
            if (!_editing)
            {
                return;
            }

            _editing = false;

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

            int palette = (int)Math.Min(int.MaxValue, Math.Max(0L, parsed));
            _controller.RoomSettings.pal = palette;

            RainWorldGame game = owner?.room?.game;
            if (game?.cameras != null && game.cameras.Length > 0 && game.cameras[0] != null)
            {
                game.cameras[0].ChangeMainPalette(_controller.RoomSettings.Palette);
            }

            _controller.Refresh();
            SyncFromVanillaLabel();
        }

        private void Cancel()
        {
            _editing = false;
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
