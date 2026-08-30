namespace DryCycle.TemperatureSystem;

/// <summary>
/// Global temperature/environment-system entry point. Individual influences live in
/// their own factor classes so gameplay systems can query them without coupling to
/// file parsing or DevInterface implementation details.
/// </summary>
internal static class TemperatureSystemRuntime
{
    private static bool _enabled;

    internal static void Enable()
    {
        if (_enabled)
        {
            return;
        }

        _enabled = true;
        TemperatureSetsLoader.Enable();
        SolarShadeZoneHooks.Enable();
        PlayerThermalModel.Enable();
        TemperatureDeveloperHud.Enable();
    }

    internal static void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        _enabled = false;
        TemperatureDeveloperHud.Disable();
        PlayerThermalModel.Disable();
        SolarShadeZoneHooks.Disable();
        TemperatureSetsLoader.Disable();
    }

    internal static float GetRoomHeat(Room room)
    {
        return RoomHeatFactor.GetRoomHeat(room);
    }

    internal static float GetSunlightIntensity(Room room)
    {
        return SolarEnvironment.GetSunlightIntensity(room);
    }

    internal static float GetRoomShade(Room room)
    {
        return SolarEnvironment.GetRoomShade(room);
    }

    internal static float GetLocalShade(Player player)
    {
        return SolarEnvironment.GetLocalShade(player);
    }

    internal static float GetEffectiveSunlight(Player player)
    {
        return SolarEnvironment.GetEffectiveSunlight(player);
    }

    internal static float GetRoomHumidity(Room room)
    {
        return HumidityEnvironment.GetRoomHumidity(room);
    }

    internal static float GetEffectiveHumidity(Player player)
    {
        return HumidityEnvironment.GetEffectiveHumidity(player);
    }

    internal static float GetEffectiveHumidity(Player player, int bodyIndex)
    {
        return HumidityEnvironment.GetEffectiveHumidity(player, bodyIndex);
    }

    internal static float GetHumidityBaseWaterLossMultiplier(Player player)
    {
        return HumidityEnvironment.GetBaseWaterLossMultiplier(player);
    }

    internal static float GetBodyHeat(Player player, int bodyIndex)
    {
        return PlayerThermalModel.GetBodyHeat(player, bodyIndex);
    }
}
