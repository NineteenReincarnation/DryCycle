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
        TemperatureSetsLoader.Disable();
    }

    /// <summary>
    /// Returns the authored base heat of the room in [-1, 1]. Rooms not present in
    /// TemperatureSets.txt return zero.
    /// </summary>
    internal static float GetRoomHeat(Room room)
    {
        return RoomHeatFactor.GetRoomHeat(room);
    }

    /// <summary>
    /// Returns one of the player's two runtime BodyHeat values. New player objects
    /// start at zero. Index 0 maps to BodyHeat0; any positive index maps to BodyHeat1.
    /// </summary>
    internal static float GetBodyHeat(Player player, int bodyIndex)
    {
        return PlayerThermalModel.GetBodyHeat(player, bodyIndex);
    }
}
