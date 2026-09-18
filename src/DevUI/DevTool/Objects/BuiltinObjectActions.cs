using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Session-aware builtin object actions that cannot live in an Inspector adapter because they need
/// Room/runtime access. The Inspector publishes detached Action properties; EditorActions dispatches
/// them here inside the ordinary object History boundary.
/// </summary>
internal static class BuiltinObjectActions
{
    internal const string UrbanCandlesSpawn = "builtin.urbanCandles.spawn";
    internal const string UrbanCandlesRemove = "builtin.urbanCandles.remove";
    internal const string UrbanCandlesRemoveAll = "builtin.urbanCandles.removeAll";

    internal static bool TryInvoke(
        EditorSession session,
        PlacedObject target,
        string key)
    {
        if (!ModManager.Watcher ||
            session?.Room == null ||
            target?.data is not Watcher.UrbanCandlePlacer.UrbanCandlePlacerData data)
            return false;

        data.pos = target.pos;
        data.radius = data.handlePos.magnitude;

        switch (key)
        {
            case UrbanCandlesSpawn:
                Watcher.UrbanCandlePlacer.AddCandles(
                    data.candles,
                    session.Room,
                    data.brushHandlePos,
                    data.radius,
                    data.depthMin,
                    data.depthMax,
                    data.heightMin,
                    data.heightMax,
                    data.widthMin,
                    data.widthMax,
                    data.rotation,
                    data.rotationVariation,
                    data.maxCandles);
                Watcher.UrbanCandlePlacer.RespawnCandles(
                    data.placedCandles,
                    data.candles,
                    session.Room);
                return true;

            case UrbanCandlesRemove:
                Watcher.UrbanCandlePlacer.RemoveCandles(
                    data.candles,
                    data.brushHandlePos,
                    data.radius,
                    data.depthMin,
                    data.depthMax);
                Watcher.UrbanCandlePlacer.RespawnCandles(
                    data.placedCandles,
                    data.candles,
                    session.Room);
                return true;

            case UrbanCandlesRemoveAll:
                data.candles.Clear();
                Watcher.UrbanCandlePlacer.RespawnCandles(
                    data.placedCandles,
                    data.candles,
                    session.Room);
                return true;

            default:
                return false;
        }
    }
}
