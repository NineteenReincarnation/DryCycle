using System;
using System.Globalization;
using DevInterface;
using DryCycle.DevUI.Controls;
using DryCycle.Misc;
using UnityEngine;

namespace DryCycle.DayNight;

internal static class DayNightPaletteDevUI
{
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
        DevInterface.DevUI owner,
        string IDstring,
        DevUINode parentNode,
        string name)
    {
        orig(self, owner, IDstring, parentNode, name);

        // Keep this next to the vanilla PALETTE panel so a room author can see all
        // three authored time-of-day palettes in one place. Base Palette deliberately
        // reuses Rain World's native Palette field; only Dusk and Night are DryCycle
        // additions to the room settings file.
        Panel panel = new(
            owner,
            "DryCycle_DayNight_Palette_Panel",
            self,
            new Vector2(270f, 190f),
            new Vector2(230f, 85f),
            "DAY / NIGHT PALETTES");

        PaletteController baseController = new(
            owner,
            "DryCycle_Base_Palette",
            panel,
            new Vector2(5f, panel.size.y - 20f),
            "Base Palette: ",
            0);
        panel.subNodes.Add(baseController);
        PaletteDirectInputRuntime.AttachPaletteController(owner, baseController);

        DayNightPaletteController duskController = new(
            owner,
            "DryCycle_Dusk_Palette",
            panel,
            new Vector2(5f, panel.size.y - 40f),
            "Dusk Palette: ",
            DayNightPaletteSlot.Dusk);
        panel.subNodes.Add(duskController);
        AttachDayNightInput(owner, duskController, DayNightPaletteSlot.Dusk);

        DayNightPaletteController nightController = new(
            owner,
            "DryCycle_Night_Palette",
            panel,
            new Vector2(5f, panel.size.y - 60f),
            "Night Palette: ",
            DayNightPaletteSlot.Night);
        panel.subNodes.Add(nightController);
        AttachDayNightInput(owner, nightController, DayNightPaletteSlot.Night);

        self.subNodes.Add(panel);
    }

    private static void AttachDayNightInput(
        DevInterface.DevUI owner,
        DayNightPaletteController controller,
        DayNightPaletteSlot slot)
    {
        IntegerControlInputBinding.Attach(
            owner,
            controller,
            () =>
            {
                RoomSettings roomSettings = owner?.room?.roomSettings;
                DayNightPaletteSettings.Values values = DayNightPaletteSettings.Get(roomSettings);
                return slot == DayNightPaletteSlot.Dusk
                    ? values.DuskPalette
                    : values.NightPalette;
            },
            value =>
            {
                RoomSettings roomSettings = owner?.room?.roomSettings;
                if (roomSettings == null)
                {
                    return;
                }

                DayNightPaletteSettings.Values values = DayNightPaletteSettings.Get(roomSettings);
                if (slot == DayNightPaletteSlot.Dusk)
                {
                    values.DuskPalette = value;
                }
                else
                {
                    values.NightPalette = value;
                }

                RefreshLighting(owner);
            },
            minValue: 0,
            maxValue: int.MaxValue);
    }

    private static void RefreshLighting(DevInterface.DevUI owner)
    {
        if (owner?.room?.game?.cameras != null && owner.room.game.cameras.Length > 0)
        {
            PaletteLighting.ForceRefresh(owner.room.game.cameras[0]);
        }
    }

    private enum DayNightPaletteSlot
    {
        Dusk,
        Night
    }

    private sealed class DayNightPaletteController : IntegerControl
    {
        private readonly DayNightPaletteSlot _slot;

        public DayNightPaletteController(
            DevInterface.DevUI owner,
            string IDstring,
            DevUINode parentNode,
            Vector2 pos,
            string title,
            DayNightPaletteSlot slot)
            : base(owner, IDstring, parentNode, pos, title)
        {
            _slot = slot;
            Refresh();
        }

        public override void Refresh()
        {
            RoomSettings roomSettings = owner?.room?.roomSettings;
            DayNightPaletteSettings.Values values = DayNightPaletteSettings.Get(roomSettings);
            int palette = _slot == DayNightPaletteSlot.Dusk
                ? values.DuskPalette
                : values.NightPalette;

            NumberLabelText = palette.ToString(CultureInfo.InvariantCulture);
            base.Refresh();
        }

        public override void Increment(int change)
        {
            RoomSettings roomSettings = owner?.room?.roomSettings;
            if (roomSettings == null)
            {
                return;
            }

            DayNightPaletteSettings.Values values = DayNightPaletteSettings.Get(roomSettings);
            if (_slot == DayNightPaletteSlot.Dusk)
            {
                values.DuskPalette = Math.Max(0, values.DuskPalette + change);
            }
            else
            {
                values.NightPalette = Math.Max(0, values.NightPalette + change);
            }

            Refresh();
            RefreshLighting(owner);
        }
    }
}
