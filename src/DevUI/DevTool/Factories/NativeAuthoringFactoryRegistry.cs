using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Factories;

/// <summary>
/// Backend-neutral registration point for native model factories. Built-in Rain World/DLC/Watcher
/// construction remains the default path in each concrete factory; registrations here override that
/// path by semantic ExtEnum ID and therefore never need DevInterface Page/Panel materialization.
///
/// This registry is intentionally internal for now. Once the provider contracts survive the native
/// Gizmo/transaction migration, the frozen public Extension API can expose scoped wrappers without
/// changing the authoring pipeline again.
/// </summary>
internal static class NativeAuthoringFactoryRegistry
{
    internal delegate bool PlacedObjectFactory(
        EditorSession session,
        PlacedObject.Type type,
        Vector2 worldPosition,
        out PlacedObject created);

    internal delegate bool SoundFactory(
        EditorSession session,
        string sample,
        int soundType,
        out AmbientSound created);

    internal delegate bool TriggerFactory(
        EditorSession session,
        EventTrigger.TriggerType type,
        out EventTrigger created);

    internal delegate bool TriggeredEventFactory(
        EditorSession session,
        EventTrigger owner,
        TriggeredEvent.EventType type,
        out TriggeredEvent created);

    internal delegate bool RoomEffectFactory(
        EditorSession session,
        RoomSettings.RoomEffect.Type type,
        out RoomSettings.RoomEffect created);

    private sealed class Entry<TFactory> where TFactory : Delegate
    {
        internal TFactory Factory;
        internal int Priority;
        internal long Order;
    }

    private static readonly Dictionary<string, List<Entry<PlacedObjectFactory>>> PlacedObjects =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<int, List<Entry<SoundFactory>>> Sounds = new();
    private static readonly Dictionary<string, List<Entry<TriggerFactory>>> Triggers =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<Entry<TriggeredEventFactory>>> TriggeredEvents =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<Entry<RoomEffectFactory>>> RoomEffects =
        new(StringComparer.Ordinal);
    private static long registrationOrder;

    internal static IDisposable RegisterPlacedObject(string type, PlacedObjectFactory factory, int priority = 100) =>
        Register(PlacedObjects, Normalize(type), factory, priority);

    internal static IDisposable RegisterSound(int soundType, SoundFactory factory, int priority = 100) =>
        Register(Sounds, soundType, factory, priority);

    internal static IDisposable RegisterTrigger(string type, TriggerFactory factory, int priority = 100) =>
        Register(Triggers, Normalize(type), factory, priority);

    internal static IDisposable RegisterTriggeredEvent(string type, TriggeredEventFactory factory, int priority = 100) =>
        Register(TriggeredEvents, Normalize(type), factory, priority);

    internal static IDisposable RegisterRoomEffect(string type, RoomEffectFactory factory, int priority = 100) =>
        Register(RoomEffects, Normalize(type), factory, priority);

    internal static bool TryCreatePlacedObject(
        EditorSession session,
        PlacedObject.Type type,
        Vector2 worldPosition,
        out PlacedObject created)
    {
        created = null;
        return type != null && TryInvoke(
            PlacedObjects,
            Normalize(type.value),
            factory => factory(session, type, worldPosition, out created));
    }

    internal static bool TryCreateSound(
        EditorSession session,
        string sample,
        int soundType,
        out AmbientSound created)
    {
        created = null;
        return TryInvoke(
            Sounds,
            soundType,
            factory => factory(session, sample, soundType, out created));
    }

    internal static bool TryCreateTrigger(
        EditorSession session,
        EventTrigger.TriggerType type,
        out EventTrigger created)
    {
        created = null;
        return type != null && TryInvoke(
            Triggers,
            Normalize(type.value),
            factory => factory(session, type, out created));
    }

    internal static bool TryCreateTriggeredEvent(
        EditorSession session,
        EventTrigger owner,
        TriggeredEvent.EventType type,
        out TriggeredEvent created)
    {
        created = null;
        return type != null && TryInvoke(
            TriggeredEvents,
            Normalize(type.value),
            factory => factory(session, owner, type, out created));
    }

    internal static bool TryCreateRoomEffect(
        EditorSession session,
        RoomSettings.RoomEffect.Type type,
        out RoomSettings.RoomEffect created)
    {
        created = null;
        return type != null && TryInvoke(
            RoomEffects,
            Normalize(type.value),
            factory => factory(session, type, out created));
    }

    internal static void ResetRuntimeState()
    {
        // Registrations describe process/mod lifetime capabilities rather than an editor session.
        // Do not clear them during DevUI close/reopen. This method only exists as an explicit marker
        // so runtime reset code never starts treating provider ownership as session state.
    }

    private static IDisposable Register<TKey, TFactory>(
        Dictionary<TKey, List<Entry<TFactory>>> table,
        TKey key,
        TFactory factory,
        int priority)
        where TFactory : Delegate
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (!table.TryGetValue(key, out List<Entry<TFactory>> entries))
        {
            entries = new List<Entry<TFactory>>();
            table.Add(key, entries);
        }

        Entry<TFactory> entry = new()
        {
            Factory = factory,
            Priority = priority,
            Order = registrationOrder++
        };
        entries.Add(entry);
        entries.Sort(CompareEntries);
        return new Registration(() =>
        {
            if (!table.TryGetValue(key, out List<Entry<TFactory>> current)) return;
            current.Remove(entry);
            if (current.Count == 0) table.Remove(key);
        });
    }

    private static bool TryInvoke<TKey, TFactory>(
        Dictionary<TKey, List<Entry<TFactory>>> table,
        TKey key,
        Func<TFactory, bool> invoke)
        where TFactory : Delegate
    {
        if (!table.TryGetValue(key, out List<Entry<TFactory>> entries) || entries.Count == 0)
            return false;

        // Providers are isolated: one broken optional registration must not block lower-priority
        // providers or force the whole editor into legacy mode.
        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                if (invoke(entries[i].Factory)) return true;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native authoring provider failed: " + error.Message);
            }
        }
        return false;
    }

    private static int CompareEntries<TFactory>(Entry<TFactory> a, Entry<TFactory> b)
        where TFactory : Delegate
    {
        int priority = b.Priority.CompareTo(a.Priority);
        return priority != 0 ? priority : b.Order.CompareTo(a.Order);
    }

    private static string Normalize(string value) => value?.Trim() ?? string.Empty;

    private sealed class Registration : IDisposable
    {
        private Action dispose;

        internal Registration(Action dispose) => this.dispose = dispose;

        public void Dispose()
        {
            Action action = dispose;
            if (action == null) return;
            dispose = null;
            action();
        }
    }
}
