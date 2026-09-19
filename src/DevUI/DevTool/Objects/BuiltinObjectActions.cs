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
    internal const string FloatingDebrisNewSeed = "builtin.floatingDebris.newSeed";
    internal const string FloatingDebrisAddLeft = "builtin.floatingDebris.addLeft";
    internal const string FloatingDebrisAddRight = "builtin.floatingDebris.addRight";
    internal const string FloatingDebrisRemoveLeft = "builtin.floatingDebris.removeLeft";
    internal const string FloatingDebrisRemoveRight = "builtin.floatingDebris.removeRight";

    internal static bool TryInvoke(
        EditorSession session,
        PlacedObject target,
        string key)
    {
        if (!ModManager.Watcher || session?.Room == null || target == null)
            return false;

        if (target.data is Watcher.UrbanCandlePlacer.UrbanCandlePlacerData candles)
            return TryInvokeUrbanCandles(session, target, candles, key);

        if (target.data is Watcher.FloatingDebrisData debris)
            return TryInvokeFloatingDebris(debris, key);

        return false;
    }

    private static bool TryInvokeUrbanCandles(
        EditorSession session,
        PlacedObject target,
        Watcher.UrbanCandlePlacer.UrbanCandlePlacerData data,
        string key)
    {
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

    private static bool TryInvokeFloatingDebris(
        Watcher.FloatingDebrisData data,
        string key)
    {
        if (data == null)
            return false;

        switch (key)
        {
            case FloatingDebrisNewSeed:
                data.seed = UnityEngine.Random.Range(0, int.MaxValue);
                return true;
            case FloatingDebrisAddLeft:
                data.AddControlPointLeft();
                return true;
            case FloatingDebrisAddRight:
                data.AddControlPointRight();
                return true;
            case FloatingDebrisRemoveLeft:
                if (data.controlPointPosX?.Count <= 1) return false;
                data.RemoveControlPointLeft();
                return true;
            case FloatingDebrisRemoveRight:
                if (data.controlPointPosX?.Count <= 1) return false;
                data.RemoveControlPointRight();
                return true;
            default:
                return false;
        }
    }
}
