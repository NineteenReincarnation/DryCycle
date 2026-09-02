using System;
using DevInterface;
using UnityEngine;
using DevUIOwner = DevInterface.DevUI;

namespace DryCycle.DevUI.Controls;

/// <summary>
/// Adapts any vanilla IntegerControl by replacing only its Number label with a
/// DryCycleIntegerField. The original title, arrow buttons, modifier stepping and
/// controller Increment implementation remain untouched.
/// </summary>
internal static class IntegerControlInputBinding
{
    internal static DryCycleIntegerField Attach(
        DevUIOwner owner,
        IntegerControl controller,
        Func<int> readValue,
        Action<int> writeValue,
        int minValue = int.MinValue,
        int maxValue = int.MaxValue,
        Func<string> idleDisplayProvider = null)
    {
        if (owner == null || controller == null || readValue == null || writeValue == null)
        {
            return null;
        }

        if (controller.subNodes == null || controller.subNodes.Count < 2)
        {
            return null;
        }

        if (controller.subNodes[1] is DryCycleIntegerField existing)
        {
            existing.IdleDisplayProvider = idleDisplayProvider;
            return existing;
        }

        DevUINode oldNumber = controller.subNodes[1];
        Vector2 pos = oldNumber is PositionedDevUINode positioned
            ? positioned.pos
            : new Vector2(140f, 0f);
        float width = oldNumber is RectangularDevUINode rectangular
            ? rectangular.size.x
            : 36f;

        int initial;
        try
        {
            initial = readValue();
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogError($"DryCycle DevUI: failed to read initial value for {controller.IDstring}: {ex}");
            return null;
        }

        oldNumber?.ClearSprites();

        DryCycleIntegerField field = new(
            owner,
            oldNumber?.IDstring ?? "Number",
            controller,
            pos,
            width,
            initial,
            minValue,
            maxValue,
            readValue,
            value =>
            {
                writeValue(value);
                controller.Refresh();
            })
        {
            IdleDisplayProvider = idleDisplayProvider
        };

        controller.subNodes[1] = field;
        controller.Refresh();
        return field;
    }
}
