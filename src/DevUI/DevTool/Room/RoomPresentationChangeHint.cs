using System;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Room;

/// <summary>
/// Optional semantic hint carried from a known Room edit into immutable presentation capture.
/// Hints only reduce work; absence, ambiguity or a mixed unsupported mutation always falls back to
/// a full capture. This keeps legacy/third-party writers authoritative rather than guessing dirtiness.
/// </summary>
internal readonly struct RoomPresentationChangeHint
{
    internal RoomPresentationChangeHint(
        bool full,
        bool values,
        bool overrides,
        bool fadePalette,
        bool terrainFadePalette,
        bool effects,
        int effectRowIndex)
    {
        Full = full;
        Values = values;
        Overrides = overrides;
        FadePalette = fadePalette;
        TerrainFadePalette = terrainFadePalette;
        Effects = effects;
        EffectRowIndex = effectRowIndex;
    }

    internal bool Full { get; }
    internal bool Values { get; }
    internal bool Overrides { get; }
    internal bool FadePalette { get; }
    internal bool TerrainFadePalette { get; }
    internal bool Effects { get; }

    // >= 0: only that logical RoomEffect row changed.
    // -1: no single-row edit. Effects=true then means collection/full effect rebuild.
    internal int EffectRowIndex { get; }

    internal bool HasChanges =>
        Full || Values || Overrides || FadePalette || TerrainFadePalette || Effects || EffectRowIndex >= 0;
}

internal static class RoomPresentationChangeHintHub
{
    private sealed class State
    {
        internal bool Full;
        internal bool Values;
        internal bool Overrides;
        internal bool FadePalette;
        internal bool TerrainFadePalette;
        internal bool Effects;
        internal int EffectRowIndex = -1;
        internal bool HasEffectRow;

        internal RoomPresentationChangeHint Consume()
        {
            int row = HasEffectRow && !Effects ? EffectRowIndex : -1;
            RoomPresentationChangeHint result = new(
                Full,
                Values,
                Overrides,
                FadePalette,
                TerrainFadePalette,
                Effects,
                row);
            Reset();
            return result;
        }

        internal void Reset()
        {
            Full = false;
            Values = false;
            Overrides = false;
            FadePalette = false;
            TerrainFadePalette = false;
            Effects = false;
            EffectRowIndex = -1;
            HasEffectRow = false;
        }
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static void MarkSetting(EditorSession session, string key)
    {
        if (session == null) return;
        if (key == RoomSettingKeys.FadePalette)
        {
            MarkPaletteFade(session, terrain: false);
            return;
        }
        if (key == RoomSettingKeys.TerrainFadePalette)
        {
            MarkPaletteFade(session, terrain: true);
            return;
        }

        State state = Get(session);
        state.Values = true;

        // Ordinary nullable RoomSettings fields participate in LocalSettingKeys / template override
        // badges. Recomputing those two short arrays is still much cheaper than rebuilding Effects,
        // template catalogs and palette arrays.
        if (AffectsOverrideState(key))
            state.Overrides = true;
    }

    internal static void MarkPaletteFade(EditorSession session, bool terrain)
    {
        if (session == null) return;
        State state = Get(session);
        state.Values = true;
        if (terrain) state.TerrainFadePalette = true;
        else state.FadePalette = true;
    }

    internal static void MarkEffectAmount(EditorSession session, int logicalIndex)
    {
        if (session == null || logicalIndex < 0) { MarkEffects(session); return; }
        State state = Get(session);
        if (state.Full || state.Effects) return;

        if (!state.HasEffectRow)
        {
            state.HasEffectRow = true;
            state.EffectRowIndex = logicalIndex;
            return;
        }

        // Two different effect rows in one DevUI update are still uncommon. Rebuild the effect
        // payload once rather than maintaining a transient index set.
        if (state.EffectRowIndex != logicalIndex)
        {
            state.HasEffectRow = false;
            state.EffectRowIndex = -1;
            state.Effects = true;
        }
    }

    internal static void MarkEffects(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        state.Effects = true;
        state.HasEffectRow = false;
        state.EffectRowIndex = -1;
    }

    internal static void MarkFull(EditorSession session)
    {
        if (session == null) return;
        State state = Get(session);
        state.Full = true;
        state.Values = true;
        state.Overrides = true;
        state.FadePalette = true;
        state.TerrainFadePalette = true;
        state.Effects = true;
        state.HasEffectRow = false;
        state.EffectRowIndex = -1;
    }

    internal static RoomPresentationChangeHint Consume(EditorSession session) =>
        session == null ? default : Get(session).Consume();

    internal static void Clear(EditorSession session)
    {
        if (session != null && states.TryGetValue(session, out State state))
            state.Reset();
    }

    internal static void Reset() =>
        states = new ConditionalWeakTable<EditorSession, State>();

    private static State Get(EditorSession session) =>
        states.GetValue(session, _ => new State());

    private static bool AffectsOverrideState(string key) =>
        key != RoomSettingKeys.RoomSpecificScript &&
        key != RoomSettingKeys.WetTerrain;
}
