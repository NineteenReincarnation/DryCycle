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
        if (type == null || !PlacedObjects.TryGetValue(Normalize(type.value), out List<Entry<PlacedObjectFactory>> entries))
            return false;

        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                if (entries[i].Factory(session, type, worldPosition, out PlacedObject candidate) && candidate != null)
                {
                    created = candidate;
                    return true;
                }
            }
            catch (Exception error)
            {
                LogProviderFailure("placed object", type.value, error);
            }
        }
        return false;
    }

    internal static bool TryCreateSound(
        EditorSession session,
        string sample,
        int soundType,
        out AmbientSound created)
    {
        created = null;
        if (!Sounds.TryGetValue(soundType, out List<Entry<SoundFactory>> entries))
            return false;

        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                if (entries[i].Factory(session, sample, soundType, out AmbientSound candidate) && candidate != null)
                {
                    created = candidate;
                    return true;
                }
            }
            catch (Exception error)
            {
                LogProviderFailure("sound", soundType.ToString(), error);
            }
        }
        return false;
    }

    internal static bool TryCreateTrigger(
        EditorSession session,
        EventTrigger.TriggerType type,
        out EventTrigger created)
    {
        created = null;
        if (type == null || !Triggers.TryGetValue(Normalize(type.value), out List<Entry<TriggerFactory>> entries))
            return false;

        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                if (entries[i].Factory(session, type, out EventTrigger candidate) && candidate != null)
                {
                    created = candidate;
                    return true;
                }
            }
            catch (Exception error)
            {
                LogProviderFailure("trigger", type.value, error);
            }
        }
        return false;
    }

    internal static bool TryCreateTriggeredEvent(
        EditorSession session,
        EventTrigger owner,
        TriggeredEvent.EventType type,
        out TriggeredEvent created)
    {
        created = null;
        if (type == null || !TriggeredEvents.TryGetValue(Normalize(type.value), out List<Entry<TriggeredEventFactory>> entries))
            return false;

        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                if (entries[i].Factory(session, owner, type, out TriggeredEvent candidate) && candidate != null)
                {
                    created = candidate;
                    return true;
                }
            }
            catch (Exception error)
            {
                LogProviderFailure("triggered event", type.value, error);
            }
        }
        return false;
    }

    internal static bool TryCreateRoomEffect(
        EditorSession session,
        RoomSettings.RoomEffect.Type type,
        out RoomSettings.RoomEffect created)
    {
        created = null;
        if (type == null || !RoomEffects.TryGetValue(Normalize(type.value), out List<Entry<RoomEffectFactory>> entries))
            return false;

        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                if (entries[i].Factory(session, type, out RoomSettings.RoomEffect candidate) && candidate != null)
                {
                    created = candidate;
                    return true;
                }
            }
            catch (Exception error)
            {
                LogProviderFailure("room effect", type.value, error);
            }
        }
        return false;
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

    private static int CompareEntries<TFactory>(Entry<TFactory> a, Entry<TFactory> b)
        where TFactory : Delegate
    {
        int priority = b.Priority.CompareTo(a.Priority);
        return priority != 0 ? priority : b.Order.CompareTo(a.Order);
    }

    private static string Normalize(string value) => value?.Trim() ?? string.Empty;

    private static void LogProviderFailure(string kind, string id, Exception error) =>
        Plugin.Logger?.LogWarning(
            "DevTool native " + kind + " provider failed for '" + (id ?? string.Empty) + "': " + error.Message);

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
