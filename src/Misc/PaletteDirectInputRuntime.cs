using System;
using System.Collections.Generic;
using System.Globalization;
using DevInterface;
using DryCycle.DevUI.Controls;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.Misc;

/// <summary>
/// Wires Rain World's vanilla palette IntegerControls into DryCycle's reusable
/// numeric input field without changing their arrow/inheritance behavior.
/// </summary>
internal static class PaletteDirectInputRuntime
{
    private static bool _enabled;

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

    internal static DryCycleIntegerField AttachPaletteController(DevUIOwner owner, PaletteController controller)
    {
        if (owner == null || controller == null || controller.controlPoint < 0 || controller.controlPoint > 3)
        {
            return null;
        }

        return IntegerControlInputBinding.Attach(
            owner,
            controller,
            () => ReadPaletteValue(controller),
            value => ApplyPaletteValue(owner, controller, value),
            minValue: 0,
            maxValue: int.MaxValue,
            idleDisplayProvider: () => BuildIdleDisplay(controller));
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

    private static int ReadPaletteValue(PaletteController controller)
    {
        RoomSettings settings = controller?.RoomSettings;
        if (settings == null)
        {
            return 0;
        }

        return controller.controlPoint switch
        {
            0 => Math.Max(0, settings.Palette),
            1 => Math.Max(0, settings.EffectColorA),
            2 => Math.Max(0, settings.EffectColorB),
            3 => settings.fadePalette == null ? 0 : Math.Max(0, settings.fadePalette.palette),
            _ => 0
        };
    }

    private static string BuildIdleDisplay(PaletteController controller)
    {
        RoomSettings settings = controller?.RoomSettings;
        if (settings == null)
        {
            return null;
        }

        switch (controller.controlPoint)
        {
            case 0:
                return BuildInheritedDisplay(settings.pal.HasValue, settings.parent?.pal.HasValue == true, settings.parent?.isAncestor == true, settings.Palette);
            case 1:
                return BuildInheritedDisplay(settings.eColA.HasValue, settings.parent?.eColA.HasValue == true, settings.parent?.isAncestor == true, settings.EffectColorA);
            case 2:
                return BuildInheritedDisplay(settings.eColB.HasValue, settings.parent?.eColB.HasValue == true, settings.parent?.isAncestor == true, settings.EffectColorB);
            case 3:
                return settings.fadePalette == null ? "NONE" : settings.fadePalette.palette.ToString(CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    private static string BuildInheritedDisplay(bool hasLocal, bool parentHasValue, bool parentIsAncestor, int value)
    {
        if (hasLocal)
        {
            return " " + value.ToString(CultureInfo.InvariantCulture);
        }

        string prefix = parentIsAncestor || !parentHasValue ? "<A>" : "<T>";
        return prefix + " " + value.ToString(CultureInfo.InvariantCulture);
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
}
