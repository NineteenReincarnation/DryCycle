using Menu.Remix.MixedUI;
using UnityEngine;

namespace DryCycle.Misc;

/// <summary>
/// Remix options for DryCycle gameplay/editor behavior.
/// </summary>
internal sealed class DryCycleOptions : OptionInterface
{
    private static DryCycleOptions _instance;

    internal readonly Configurable<bool> UnlockCtrlZGameplayInput;
    internal readonly Configurable<bool> UnlockCtrlSGameplayInput;
    internal readonly Configurable<bool> UnlockCtrlYGameplayInput;
    internal readonly Configurable<bool> RopeSpearEightDirectionThrow;

    internal static bool CtrlZGameplayUnlocked => _instance?.UnlockCtrlZGameplayInput?.Value ?? false;
    internal static bool CtrlSGameplayUnlocked => _instance?.UnlockCtrlSGameplayInput?.Value ?? false;
    internal static bool CtrlYGameplayUnlocked => _instance?.UnlockCtrlYGameplayInput?.Value ?? false;

    /// <summary>
    /// False is the authored default: hold X to sweep continuously through the arc.
    /// True switches RopeSpear aiming to the eight digital movement directions.
    /// </summary>
    internal static bool RopeSpearEightDirectionThrowEnabled
        => _instance?.RopeSpearEightDirectionThrow?.Value ?? false;

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

        RopeSpearEightDirectionThrow = config.Bind(
            "RopeSpearEightDirectionThrow",
            false,
            new ConfigurableInfo(
                "Switch RopeSpear aiming from the default continuous hold-X sweep to eight-direction aiming. In eight-direction mode, hold X and use the movement directions; for example Up+Right throws diagonally up-right."));
    }

    internal static void Register()
    {
        OptionInterface registered = MachineConnector.GetRegisteredOI(Plugin.RainWorldModId);
        if (_instance != null && ReferenceEquals(registered, _instance))
        {
            return;
        }

        if (registered is DryCycleOptions existing)
        {
            _instance = existing;
            return;
        }

        _instance = null;
        DryCycleOptions options = new();
        if (!MachineConnector.SetRegisteredOI(Plugin.RainWorldModId, options))
        {
            Plugin.Logger?.LogWarning("DryCycle could not register its Remix options page; gameplay options will use their defaults.");
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
            new OpTab(this, "DevUI Input"),
            new OpTab(this, "Rope Spear")
        };

        BuildDevUiInputTab(Tabs[0]);
        BuildRopeSpearTab(Tabs[1]);
    }

    private void BuildDevUiInputTab(OpTab tab)
    {
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
            FLabelAlignment.Center)
        {
            autoWrap = true
        };

        AddCheckBoxRow(tab, UnlockCtrlZGameplayInput, 355f, "Unlock Ctrl+Z gameplay input");
        AddCheckBoxRow(tab, UnlockCtrlSGameplayInput, 300f, "Unlock Ctrl+S gameplay input");
        AddCheckBoxRow(tab, UnlockCtrlYGameplayInput, 245f, "Unlock Ctrl+Y gameplay input");

        tab.AddItems(title, explanation);
    }

    private void BuildRopeSpearTab(OpTab tab)
    {
        OpLabel title = new(
            new Vector2(100f, 515f),
            new Vector2(400f, 35f),
            "Rope Spear Throwing",
            FLabelAlignment.Center,
            bigText: true);

        OpLabel explanation = new(
            new Vector2(70f, 430f),
            new Vector2(460f, 70f),
            "Default: hold Throw (X) to use the current continuous sweeping aim. Enable eight-direction throwing to aim directly with movement input while holding Throw.",
            FLabelAlignment.Center)
        {
            autoWrap = true
        };

        OpCheckBox eightDirection = new(
            RopeSpearEightDirectionThrow,
            new Vector2(135f, 345f));
        eightDirection.description =
            "Use eight-direction RopeSpear aiming. Up+Right = 45 degree up-right, Down+Left = 45 degree down-left, and so on. No direction input uses the slugcat's facing direction.";

        OpLabel label = new(
            new Vector2(175f, 342f),
            new Vector2(320f, 30f),
            "Eight-direction throwing",
            FLabelAlignment.Left)
        {
            bumpBehav = eightDirection.bumpBehav,
            description = eightDirection.description
        };

        OpLabel mapping = new(
            new Vector2(95f, 230f),
            new Vector2(410f, 85f),
            "Directions:  ↑  ↓  ←  →\nDiagonals:  ↑+←  ↑+→  ↓+←  ↓+→",
            FLabelAlignment.Center)
        {
            autoWrap = true
        };

        tab.AddItems(title, explanation, eightDirection, label, mapping);
    }

    private static void AddCheckBoxRow(OpTab tab, Configurable<bool> configurable, float y, string label)
    {
        OpCheckBox checkBox = new(configurable, new Vector2(135f, y));
        OpLabel text = new(new Vector2(175f, y - 3f), new Vector2(300f, 30f), label, FLabelAlignment.Left);
        tab.AddItems(checkBox, text);
    }
}
