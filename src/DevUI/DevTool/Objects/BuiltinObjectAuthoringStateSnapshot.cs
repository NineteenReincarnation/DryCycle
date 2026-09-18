using System;
using System.Globalization;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// DevTool-only authoring state that Rain World's own PlacedObject.Data serialization intentionally
/// does not persist. This sidecar participates in Undo/Redo and duplication without changing the
/// vanilla room-settings file format.
/// </summary>
internal sealed class BuiltinObjectAuthoringStateSnapshot
{
    private readonly bool urbanCandlePlacer;
    private readonly float rotationVariation;
    private readonly float rotation;
    private readonly float widthMax;
    private readonly float widthMin;
    private readonly float heightMax;
    private readonly float heightMin;
    private readonly int depthMax;
    private readonly int depthMin;
    private readonly int maxCandles;

    private BuiltinObjectAuthoringStateSnapshot(
        Watcher.UrbanCandlePlacer.UrbanCandlePlacerData data)
    {
        urbanCandlePlacer = true;
        rotationVariation = data.rotationVariation;
        rotation = data.rotation;
        widthMax = data.widthMax;
        widthMin = data.widthMin;
        heightMax = data.heightMax;
        heightMin = data.heightMin;
        depthMax = data.depthMax;
        depthMin = data.depthMin;
        maxCandles = data.maxCandles;
    }

    internal string Fingerprint
    {
        get
        {
            if (!urbanCandlePlacer)
                return string.Empty;

            return string.Join(
                ",",
                rotationVariation.ToString("R", CultureInfo.InvariantCulture),
                rotation.ToString("R", CultureInfo.InvariantCulture),
                widthMax.ToString("R", CultureInfo.InvariantCulture),
                widthMin.ToString("R", CultureInfo.InvariantCulture),
                heightMax.ToString("R", CultureInfo.InvariantCulture),
                heightMin.ToString("R", CultureInfo.InvariantCulture),
                depthMax.ToString(CultureInfo.InvariantCulture),
                depthMin.ToString(CultureInfo.InvariantCulture),
                maxCandles.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal static BuiltinObjectAuthoringStateSnapshot Capture(PlacedObject.Data data)
    {
        if (ModManager.Watcher &&
            data is Watcher.UrbanCandlePlacer.UrbanCandlePlacerData candlePlacer)
            return new BuiltinObjectAuthoringStateSnapshot(candlePlacer);

        return null;
    }

    /// <summary>
    /// Some vanilla FromString implementations append into authored lists instead of replacing them.
    /// Clear only the verified list before replaying the serialized state.
    /// </summary>
    internal void PrepareSerializedRestore(PlacedObject.Data data)
    {
        if (!urbanCandlePlacer ||
            data is not Watcher.UrbanCandlePlacer.UrbanCandlePlacerData candlePlacer)
            return;

        candlePlacer.candles?.Clear();
    }

    internal void Restore(PlacedObject.Data data)
    {
        if (!urbanCandlePlacer ||
            data is not Watcher.UrbanCandlePlacer.UrbanCandlePlacerData candlePlacer)
            return;

        candlePlacer.rotationVariation = rotationVariation;
        candlePlacer.rotation = rotation;
        candlePlacer.widthMax = widthMax;
        candlePlacer.widthMin = widthMin;
        candlePlacer.heightMax = heightMax;
        candlePlacer.heightMin = heightMin;
        candlePlacer.depthMax = depthMax;
        candlePlacer.depthMin = depthMin;
        candlePlacer.maxCandles = maxCandles;
        candlePlacer.radius = candlePlacer.handlePos.magnitude;
    }
}
