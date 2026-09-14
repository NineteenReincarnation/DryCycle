using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Room;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// In-memory history for RoomSettings.effects membership. Add/remove operations are not truly
/// single-member mutations because vanilla RemoveEffect/InheritEffects can replace inherited rows
/// and change local overwrite flags. Capture the effect collection only, rather than serializing the
/// complete RoomSettings document, so that exact membership/order/flags survive Undo/Redo.
/// </summary>
internal sealed class RoomEffectCollectionStateSnapshot : IEditorStateSnapshot
{
    private sealed class Entry
    {
        internal RoomSettings.RoomEffect Target;
        internal float Amount;
        internal float[] ExtraAmounts;
        internal bool Inherited;
        internal bool OverWrite;
        internal bool Save;
        internal Vector2 PanelPosition;
    }

    private readonly RoomSettings settings;
    private readonly Entry[] entries;

    private RoomEffectCollectionStateSnapshot(RoomSettings settings, Entry[] entries, string fingerprint)
    {
        this.settings = settings;
        this.entries = entries;
        Fingerprint = fingerprint;
    }

    public string Kind =>
        "RoomEffectCollection:" + (settings == null ? 0 : RuntimeHelpers.GetHashCode(settings));

    public string Fingerprint { get; }

    internal static RoomEffectCollectionStateSnapshot Capture(RoomSettings settings)
    {
        if (settings?.effects == null) return null;

        Entry[] entries = new Entry[settings.effects.Count];
        var fingerprint = new System.Text.StringBuilder();
        for (int i = 0; i < settings.effects.Count; i++)
        {
            RoomSettings.RoomEffect effect = settings.effects[i];
            if (effect == null)
            {
                entries[i] = new Entry();
                fingerprint.Append(i).Append(":null;");
                continue;
            }

            entries[i] = new Entry
            {
                Target = effect,
                Amount = effect.amount,
                ExtraAmounts = effect.extraAmounts == null ? Array.Empty<float>() : (float[])effect.extraAmounts.Clone(),
                Inherited = effect.inherited,
                OverWrite = effect.overWrite,
                Save = effect.save,
                PanelPosition = effect.panelPosition
            };

            fingerprint.Append(i).Append(':')
                .Append(RuntimeHelpers.GetHashCode(effect)).Append(':')
                .Append(effect.type?.value ?? string.Empty).Append(':')
                .Append(effect.inherited ? '1' : '0').Append(':')
                .Append(effect.overWrite ? '1' : '0').Append(':')
                .Append(effect.save ? '1' : '0').Append(':')
                .Append(effect.amount.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(effect.panelPosition.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(effect.panelPosition.y.ToString("R", CultureInfo.InvariantCulture));

            if (effect.extraAmounts != null)
            {
                for (int n = 0; n < effect.extraAmounts.Length; n++)
                    fingerprint.Append(',').Append(effect.extraAmounts[n].ToString("R", CultureInfo.InvariantCulture));
            }
            fingerprint.Append(';');
        }

        return new RoomEffectCollectionStateSnapshot(settings, entries, fingerprint.ToString());
    }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        ReferenceEquals(session?.RoomSettings, settings) ? Capture(settings) : null;

    public bool Restore(EditorSession session)
    {
        if (!ReferenceEquals(session?.RoomSettings, settings) || settings?.effects == null)
            return false;

        try
        {
            settings.effects.Clear();
            for (int i = 0; i < entries.Length; i++)
            {
                Entry entry = entries[i];
                RoomSettings.RoomEffect effect = entry?.Target;
                if (effect == null) continue;

                effect.amount = entry.Amount;
                effect.inherited = entry.Inherited;
                effect.overWrite = entry.OverWrite;
                effect.save = entry.Save;
                effect.panelPosition = entry.PanelPosition;

                if (effect.extraAmounts != null)
                {
                    int count = Math.Min(effect.extraAmounts.Length, entry.ExtraAmounts?.Length ?? 0);
                    for (int n = 0; n < count; n++) effect.extraAmounts[n] = entry.ExtraAmounts[n];
                }

                settings.effects.Add(effect);
            }

            RoomEditorActions.RefreshLegacyPageOrDefer(session);
            RoomEffectLiveCompatibility.Reconcile(session);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool room-effect collection restore failed: " + error.Message);
            return false;
        }
    }
}
