using System;
using System.Reflection;
using DryCycle.Items.KingVultureSpear;
using DryCycle.TemperatureSystem;

namespace DryCycle.Thirst;

/// <summary>
/// Optional SlugBase compatibility for DryCycle character tuning.
///
/// When SlugBase is installed, DryCycle registers custom PlayerFeatures by
/// reflection so this assembly has no hard reference to SlugBase.dll:
/// "WaterLossRate" (WV per second), "WaterPips" (normal hibernation
/// requirement/cost in hydration pips), and "DryCycleDifficulty" (final WV
/// difficulty multiplier). Custom SlugBase characters can set these exact keys
/// in their JSON. Without SlugBase, vanilla/default values are used.
/// </summary>
internal static class SlugBaseHydrationFeatures
{
    public const float DefaultWaterLossRate = 5f;
    public const int DefaultWaterPips = 2;

    public const float DefaultDryCycleDifficulty = 1f;
    public const float MinDryCycleDifficulty = 0.5f;
    public const float MaxDryCycleDifficulty = 3f;

    private static bool _initialized;
    private static object _waterLossRateFeature;
    private static object _waterPipsFeature;
    private static object _dryCycleDifficultyFeature;
    private static MethodInfo _waterLossTryGetPlayer;
    private static MethodInfo _waterPipsTryGetPlayer;
    private static MethodInfo _difficultyTryGetPlayer;
    private static MethodInfo _waterLossTryGetCharacter;
    private static MethodInfo _waterPipsTryGetCharacter;
    private static MethodInfo _difficultyTryGetCharacter;
    private static MethodInfo _slugBaseCharacterGet;

    public static bool Available { get; private set; }

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            Assembly slugBaseAssembly = FindSlugBaseAssembly();
            if (slugBaseAssembly == null)
            {
                // No dependency is declared. If another plugin has not loaded
                // SlugBase yet, a later PreModsInit/OnModsInit call may retry.
                return;
            }

            _initialized = true;

            Type featureTypes = slugBaseAssembly.GetType("SlugBase.Features.FeatureTypes", throwOnError: false);
            Type characterType = slugBaseAssembly.GetType("SlugBase.SlugBaseCharacter", throwOnError: false);
            if (featureTypes == null || characterType == null)
            {
                return;
            }

            MethodInfo playerFloat = featureTypes.GetMethod(
                "PlayerFloat",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            MethodInfo playerInt = featureTypes.GetMethod(
                "PlayerInt",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            _slugBaseCharacterGet = characterType.GetMethod(
                "Get",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(SlugcatStats.Name) },
                modifiers: null);

            if (playerFloat == null || playerInt == null || _slugBaseCharacterGet == null)
            {
                return;
            }

            // Constructing the reflected feature objects registers these exact
            // JSON keys with SlugBase's FeatureManager before its JSON scan.
            _waterLossRateFeature = playerFloat.Invoke(null, new object[] { "WaterLossRate" });
            _waterPipsFeature = playerInt.Invoke(null, new object[] { "WaterPips" });
            _dryCycleDifficultyFeature = playerFloat.Invoke(null, new object[] { "DryCycleDifficulty" });

            _waterLossTryGetPlayer = FindTryGetMethod(_waterLossRateFeature, typeof(Player), typeof(float));
            _waterPipsTryGetPlayer = FindTryGetMethod(_waterPipsFeature, typeof(Player), typeof(int));
            _difficultyTryGetPlayer = FindTryGetMethod(_dryCycleDifficultyFeature, typeof(Player), typeof(float));
            _waterLossTryGetCharacter = FindTryGetMethod(_waterLossRateFeature, characterType, typeof(float));
            _waterPipsTryGetCharacter = FindTryGetMethod(_waterPipsFeature, characterType, typeof(int));
            _difficultyTryGetCharacter = FindTryGetMethod(_dryCycleDifficultyFeature, characterType, typeof(float));

            Available = _waterLossTryGetPlayer != null &&
                        _waterPipsTryGetPlayer != null &&
                        _difficultyTryGetPlayer != null &&
                        _waterLossTryGetCharacter != null &&
                        _waterPipsTryGetCharacter != null &&
                        _difficultyTryGetCharacter != null;
        }
        catch (Exception ex)
        {
            Available = false;
            Plugin.Logger?.LogWarning($"SlugBase DryCycle feature compatibility disabled: {ex}");
        }
    }

    public static float GetWaterLossRate(Player player)
    {
        if (TryGetPlayerFloat(_waterLossRateFeature, _waterLossTryGetPlayer, player, out float configured))
        {
            return Math.Max(0f, configured);
        }

        return DefaultWaterLossRate;
    }

    public static float GetWaterLossRate(SlugcatStats.Name slugcat)
    {
        if (TryGetCharacterFloat(_waterLossRateFeature, _waterLossTryGetCharacter, slugcat, out float configured))
        {
            return Math.Max(0f, configured);
        }

        return DefaultWaterLossRate;
    }

    public static int GetWaterPips(Player player)
    {
        if (TryGetPlayerInt(_waterPipsFeature, _waterPipsTryGetPlayer, player, out int configured))
        {
            return Math.Max(0, configured);
        }

        return GetDefaultWaterPips(player?.SlugCatClass);
    }

    public static int GetWaterPips(SlugcatStats.Name slugcat)
    {
        if (TryGetCharacterInt(_waterPipsFeature, _waterPipsTryGetCharacter, slugcat, out int configured))
        {
            return Math.Max(0, configured);
        }

        return GetDefaultWaterPips(slugcat);
    }

    /// <summary>
    /// Final DryCycle difficulty multiplier for this player.
    /// The authored SlugBase value is always clamped to [0.5, 3.0].
    /// Missing/unsupported values default to 1.0.
    /// </summary>
    public static float GetDryCycleDifficulty(Player player)
    {
        if (TryGetPlayerFloat(
                _dryCycleDifficultyFeature,
                _difficultyTryGetPlayer,
                player,
                out float configured))
        {
            return ClampDryCycleDifficulty(configured);
        }

        return DefaultDryCycleDifficulty;
    }

    public static float GetDryCycleDifficulty(SlugcatStats.Name slugcat)
    {
        if (TryGetCharacterFloat(
                _dryCycleDifficultyFeature,
                _difficultyTryGetCharacter,
                slugcat,
                out float configured))
        {
            return ClampDryCycleDifficulty(configured);
        }

        return DefaultDryCycleDifficulty;
    }

    public static float ClampDryCycleDifficulty(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return DefaultDryCycleDifficulty;
        }

        return Math.Max(MinDryCycleDifficulty, Math.Min(MaxDryCycleDifficulty, value));
    }

    /// <summary>
    /// The existing SlugBase-compatible base WV loss after current status effects.
    /// Temperature losses are deliberately not multiplied by those status effects.
    /// </summary>
    public static float GetBaseWaterLossRateAfterStatus(Player player)
    {
        float multiplier = KingVultureSpearCombat.GetWaterLossMultiplier(player);
        return GetWaterLossRate(player) * multiplier;
    }

    /// <summary>
    /// Final WV/second = (base WV loss + direct solar loss + BodyHeat loss)
    ///                   * DryCycleDifficulty.
    /// Difficulty is intentionally the final multiplication stage so it scales the
    /// complete result without changing the individual source calculations.
    /// </summary>
    public static float GetTotalWaterLossRate(Player player)
    {
        float rawTotal = GetBaseWaterLossRateAfterStatus(player) +
                         ThermalWaterLoss.GetTotalExtraWaterLossRate(player);
        return rawTotal * GetDryCycleDifficulty(player);
    }

    public static float GetWaterLossPerTick(Player player)
    {
        return GetTotalWaterLossRate(player) /
               ThirstConstants.WaterValuePerPip /
               ThirstConstants.SimulationTicksPerSecond;
    }

    private static int GetDefaultWaterPips(SlugcatStats.Name slugcat)
    {
        string id = slugcat?.value;

        return id switch
        {
            "Yellow" => 1,
            "White" => 2,
            "Red" => 3,
            "Gourmand" => 4,
            "Artificer" => 3,
            "Rivulet" => 3,
            "Spear" => 3,
            "Saint" => 2,
            "Inv" => 6,
            "Watcher" => 2,
            _ => DefaultWaterPips
        };
    }

    private static Assembly FindSlugBaseAssembly()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, "SlugBase", StringComparison.OrdinalIgnoreCase))
            {
                return assembly;
            }
        }

        return null;
    }

    private static MethodInfo FindTryGetMethod(object feature, Type ownerType, Type valueType)
    {
        if (feature == null || ownerType == null || valueType == null)
        {
            return null;
        }

        Type byRefValue = valueType.MakeByRefType();
        foreach (MethodInfo method in feature.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.Name != "TryGet")
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 2 &&
                parameters[0].ParameterType == ownerType &&
                parameters[1].ParameterType == byRefValue)
            {
                return method;
            }
        }

        return null;
    }

    private static object GetSlugBaseCharacter(SlugcatStats.Name slugcat)
    {
        if (!Available || slugcat == null || _slugBaseCharacterGet == null)
        {
            return null;
        }

        return _slugBaseCharacterGet.Invoke(null, new object[] { slugcat });
    }

    private static bool TryGetPlayerFloat(object feature, MethodInfo method, Player player, out float value)
    {
        value = 0f;
        if (!Available || feature == null || method == null || player == null)
        {
            return false;
        }

        object[] args = { player, 0f };
        bool found = method.Invoke(feature, args) is bool result && result;
        if (found && args[1] is float parsed)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetPlayerInt(object feature, MethodInfo method, Player player, out int value)
    {
        value = 0;
        if (!Available || feature == null || method == null || player == null)
        {
            return false;
        }

        object[] args = { player, 0 };
        bool found = method.Invoke(feature, args) is bool result && result;
        if (found && args[1] is int parsed)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetCharacterFloat(object feature, MethodInfo method, SlugcatStats.Name slugcat, out float value)
    {
        value = 0f;
        object character = GetSlugBaseCharacter(slugcat);
        if (character == null || feature == null || method == null)
        {
            return false;
        }

        object[] args = { character, 0f };
        bool found = method.Invoke(feature, args) is bool result && result;
        if (found && args[1] is float parsed)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetCharacterInt(object feature, MethodInfo method, SlugcatStats.Name slugcat, out int value)
    {
        value = 0;
        object character = GetSlugBaseCharacter(slugcat);
        if (character == null || feature == null || method == null)
        {
            return false;
        }

        object[] args = { character, 0 };
        bool found = method.Invoke(feature, args) is bool result && result;
        if (found && args[1] is int parsed)
        {
            value = parsed;
            return true;
        }

        return false;
    }
}
