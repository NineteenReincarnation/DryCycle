using Menu.Remix.MixedUI;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Small Remix page for editor-input behavior. By default Ctrl+Z/S/Y are reserved
/// while O (DevTools) + H (DevUI) mode is open; each checkbox below explicitly
/// releases one shortcut key back to the player's gameplay bindings.
/// </summary>
internal sealed class DryCycleOptions : OptionInterface
{
    private static DryCycleOptions _instance;

    internal readonly Configurable<bool> UnlockCtrlZGameplayInput;
    internal readonly Configurable<bool> UnlockCtrlSGameplayInput;
    internal readonly Configurable<bool> UnlockCtrlYGameplayInput;

    internal static bool CtrlZGameplayUnlocked => _instance?.UnlockCtrlZGameplayInput?.Value ?? false;
    internal static bool CtrlSGameplayUnlocked => _instance?.UnlockCtrlSGameplayInput?.Value ?? false;
    internal static bool CtrlYGameplayUnlocked => _instance?.UnlockCtrlYGameplayInput?.Value ?? false;

    internal DryCycleOptions()
    {
        UnlockCtrlZGameplayInput = config.Bind(
            "UnlockCtrlZGameplayInput",
            false,
            new ConfigurableInfo(
                "Allow the key bound to Z to keep controlling the player while Ctrl+Z is used in O+H DevUI mode."));

        UnlockCtrlSGameplayInput = config.Bind(
            "UnlockCtrlSGameplayInput",
            false,
            new ConfigurableInfo(
                "Allow the key bound to S to keep controlling the player while Ctrl+S is used in O+H DevUI mode."));

        UnlockCtrlYGameplayInput = config.Bind(
            "UnlockCtrlYGameplayInput",
            false,
            new ConfigurableInfo(
                "Allow the key bound to Y to keep controlling the player while Ctrl+Y is used in O+H DevUI mode."));
    }

    internal static void Register()
    {
        if (_instance != null)
        {
            return;
        }

        if (MachineConnector.GetRegisteredOI(Plugin.RainWorldModId) is DryCycleOptions existing)
        {
            _instance = existing;
            return;
        }

        DryCycleOptions options = new();
        if (!MachineConnector.SetRegisteredOI(Plugin.RainWorldModId, options))
        {
            return;
        }

        _instance = options;
        try
        {
            // Registration normally occurs before Remix loads configs, but explicitly
            // reload as well so a late registration still picks up saved values.
            _instance.config.Reload();
        }
        catch (System.Exception ex)
        {
            Plugin.Logger?.LogWarning("DryCycle Remix config reload failed: " + ex.Message);
        }
    }

    public override void Initialize()
    {
        base.Initialize();

        Tabs = new[]
        {
            new OpTab(this, "DevUI Input")
        };

        OpLabel title = new(
            new Vector2(100f, 515f),
            new Vector2(400f, 35f),
            "DryCycle DevUI Input",
            FLabelAlignment.Center,
            bigText: true);

        OpLabel explanation = new(
            new Vector2(75f, 430f),
            new Vector2(450f, 65f),
            "When DevTools (O) and DevUI (H) are both open, Ctrl+Z / Ctrl+S / Ctrl+Y reserve their letter keys so editor shortcuts cannot move the player. Enable an unlock below to release that key back to gameplay.",
            FLabelAlignment.Center);

        AddUnlockRow(Tabs[0], UnlockCtrlZGameplayInput, 355f, "Unlock Ctrl+Z gameplay input");
        AddUnlockRow(Tabs[0], UnlockCtrlSGameplayInput, 300f, "Unlock Ctrl+S gameplay input");
        AddUnlockRow(Tabs[0], UnlockCtrlYGameplayInput, 245f, "Unlock Ctrl+Y gameplay input");

        Tabs[0].AddItems(title, explanation);
    }

    private static void AddUnlockRow(OpTab tab, Configurable<bool> configurable, float y, string label)
    {
        OpCheckBox checkBox = new(configurable, new Vector2(135f, y));
        OpLabel text = new(new Vector2(175f, y - 3f), new Vector2(300f, 30f), label, FLabelAlignment.Left);
        tab.AddItems(checkBox, text);
    }
}
