namespace DryCycle.TemperatureSystem;

/// <summary>
/// Global temperature-system entry point. Individual temperature influences live in
/// their own factor classes so later player-stat, rendering and hydration effects can
/// consume them without coupling file parsing to gameplay logic.
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

    internal static float GetBodyHeat(Player player, int bodyIndex)
    {
        return PlayerThermalModel.GetBodyHeat(player, bodyIndex);
    }
}
