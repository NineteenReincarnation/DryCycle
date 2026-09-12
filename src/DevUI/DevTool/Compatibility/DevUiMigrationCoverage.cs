using System;
using System.Collections.Generic;
using System.Reflection;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>Origin of a legacy DevInterface obligation.</summary>
public enum DevUiMigrationSource
{
    Vanilla,
    RegionKit,
    DryCycle,
    Other
}

/// <summary>How an observed interactive obligation is represented in the rebuilt editor.</summary>
public enum DevUiMigrationState
{
    NativeNewUi,
    GenericAdapter,
    SpecializedAdapter,
    Unmapped
}

public sealed class DevUiMigrationCoverageEntry
{
    public string PageType { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string AssemblyName { get; init; } = string.Empty;
    public string ExamplePath { get; init; } = string.Empty;
    public string ExampleId { get; init; } = string.Empty;
    public string AdapterNote { get; init; } = string.Empty;
    public DevUiMigrationSource Source { get; init; }
    public DevUiMigrationState State { get; init; }
    public int InstanceCount { get; init; }
    public bool IsMapped => State != DevUiMigrationState.Unmapped;
}

public sealed class DevUiMigrationSourceSummary
{
    public DevUiMigrationSource Source { get; init; }
    public int TotalTypeCount { get; init; }
    public int MappedTypeCount { get; init; }
    public int UnmappedTypeCount => TotalTypeCount - MappedTypeCount;
}

/// <summary>Detached immutable coverage snapshot consumed by the optional ImGui frontend.</summary>
public sealed class DevUiMigrationCoverageSnapshot
{
    public static readonly DevUiMigrationCoverageSnapshot Empty = new()
    {
        Entries = Array.Empty<DevUiMigrationCoverageEntry>(),
        Vanilla = EmptySummary(DevUiMigrationSource.Vanilla),
        RegionKit = EmptySummary(DevUiMigrationSource.RegionKit),
        DryCycle = EmptySummary(DevUiMigrationSource.DryCycle),
        Other = EmptySummary(DevUiMigrationSource.Other)
    };

    public string PageType { get; init; } = string.Empty;
    public DevUiMigrationCoverageEntry[] Entries { get; init; } = Array.Empty<DevUiMigrationCoverageEntry>();
    public DevUiMigrationSourceSummary Vanilla { get; init; } = EmptySummary(DevUiMigrationSource.Vanilla);
    public DevUiMigrationSourceSummary RegionKit { get; init; } = EmptySummary(DevUiMigrationSource.RegionKit);
    public DevUiMigrationSourceSummary DryCycle { get; init; } = EmptySummary(DevUiMigrationSource.DryCycle);
    public DevUiMigrationSourceSummary Other { get; init; } = EmptySummary(DevUiMigrationSource.Other);

    public int TotalTypeCount => Entries?.Length ?? 0;

    public int UnmappedTypeCount
    {
        get
        {
            if (Entries == null) return 0;
            int count = 0;
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i] != null && Entries[i].State == DevUiMigrationState.Unmapped) count++;
            return count;
        }
    }

    public DevUiMigrationSourceSummary Summary(DevUiMigrationSource source) => source switch
    {
        DevUiMigrationSource.Vanilla => Vanilla,
        DevUiMigrationSource.RegionKit => RegionKit,
        DevUiMigrationSource.DryCycle => DryCycle,
        _ => Other
    };

    private static DevUiMigrationSourceSummary EmptySummary(DevUiMigrationSource source) => new()
    {
        Source = source,
        TotalTypeCount = 0,
        MappedTypeCount = 0
    };
}

/// <summary>
/// Capability-based migration audit.
///
/// The old implementation accumulated a growing list of concrete RegionKit control types. This
/// scanner instead asks a single question for every interactive node in the real runtime tree:
/// can the generic semantic bridge operate it through the same contract the legacy UI uses?
///
/// Buttons, sliders, cyclers, integer controls, select controls and structurally discoverable
/// value controls are therefore covered regardless of whether they came from vanilla, RegionKit,
/// DryCycle or another mod. Unknown custom interactive protocols remain Unmapped and are reported
/// by runtime type/path so the next missing *protocol* can be added once for every mod.
/// </summary>
public static class DevUiMigrationCoverage
{
    private sealed class Registration
    {
        internal Type Type;
        internal string FullTypeName;
        internal bool IncludeDerived;
        internal DevUiMigrationState State;
        internal string Note;
    }

    private sealed class MutableEntry
    {
        internal string PageType;
        internal string Context;
        internal Type NodeType;
        internal string ExamplePath;
        internal string ExampleId;
        internal DevUiMigrationSource Source;
        internal DevUiMigrationState State;
        internal string AdapterNote;
        internal int InstanceCount;
    }

    private static readonly object Gate = new();
    private static readonly List<Registration> Registrations = new();
    private static readonly Dictionary<string, MutableEntry> ObservedEntries = new(StringComparer.Ordinal);
    private static volatile DevUiMigrationCoverageSnapshot currentPage = DevUiMigrationCoverageSnapshot.Empty;
    private static volatile DevUiMigrationCoverageSnapshot observed = DevUiMigrationCoverageSnapshot.Empty;
    private static int lastLoggedObservedCount = -1;
    private static int lastLoggedUnmappedCount = -1;

    static DevUiMigrationCoverage()
    {
        RegisterExact(typeof(RoomSettingsPage), DevUiMigrationState.NativeNewUi, "Rebuilt Room workspace");
        RegisterExact(typeof(ObjectsPage), DevUiMigrationState.NativeNewUi, "Rebuilt Objects workspace");
        RegisterExact(typeof(SoundPage), DevUiMigrationState.NativeNewUi, "Rebuilt Sound workspace");
        RegisterExact(typeof(TriggersPage), DevUiMigrationState.NativeNewUi, "Rebuilt Triggers workspace");
        RegisterExact(typeof(MapPage), DevUiMigrationState.NativeNewUi, "Rebuilt Map workspace");
        RegisterExact(typeof(DialogPage), DevUiMigrationState.NativeNewUi, "Rebuilt Dialog workspace");
        RegisterExact(typeof(RelationshipPage), DevUiMigrationState.NativeNewUi, "Rebuilt Relationships workspace");
    }

    public static DevUiMigrationCoverageSnapshot CurrentPage => currentPage;
    public static DevUiMigrationCoverageSnapshot Observed => observed;

    public static void RegisterExact(Type type, DevUiMigrationState state, string note = null)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        Register(new Registration
        {
            Type = type,
            FullTypeName = type.FullName ?? type.Name,
            IncludeDerived = false,
            State = state,
            Note = note ?? string.Empty
        });
    }

    public static void RegisterAssignable(Type type, DevUiMigrationState state, string note = null)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        Register(new Registration
        {
            Type = type,
            FullTypeName = type.FullName ?? type.Name,
            IncludeDerived = true,
            State = state,
            Note = note ?? string.Empty
        });
    }

    public static void RegisterTypeName(string fullTypeName, DevUiMigrationState state, string note = null)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
            throw new ArgumentException("Type name is required.", nameof(fullTypeName));
        Register(new Registration
        {
            Type = null,
            FullTypeName = fullTypeName.Trim(),
            IncludeDerived = false,
            State = state,
            Note = note ?? string.Empty
        });
    }

    internal static void Observe(Page page)
    {
        if (page == null)
        {
            currentPage = DevUiMigrationCoverageSnapshot.Empty;
            return;
        }

        try
        {
            Dictionary<string, MutableEntry> pageEntries = new(StringComparer.Ordinal);
            string pageType = page.GetType().FullName ?? page.GetType().Name;
            DevUiMigrationSource pageSource = ResolveSource(page.GetType());
            Walk(page, "root", false, pageType, pageSource, pageEntries);

            lock (Gate)
            {
                foreach (KeyValuePair<string, MutableEntry> pair in pageEntries)
                {
                    MutableEntry incoming = pair.Value;
                    if (ObservedEntries.TryGetValue(pair.Key, out MutableEntry existing))
                    {
                        if (incoming.InstanceCount > existing.InstanceCount)
                            existing.InstanceCount = incoming.InstanceCount;
                        existing.State = incoming.State;
                        existing.AdapterNote = incoming.AdapterNote;
                        existing.Source = incoming.Source;
                    }
                    else
                    {
                        ObservedEntries[pair.Key] = Clone(incoming);
                    }
                }

                currentPage = BuildSnapshot(pageType, pageEntries.Values);
                observed = BuildSnapshot("Observed", ObservedEntries.Values);
                LogCoverageGrowthIfNeeded(observed);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool migration coverage scan failed: " + error.Message);
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            ObservedEntries.Clear();
            currentPage = DevUiMigrationCoverageSnapshot.Empty;
            observed = DevUiMigrationCoverageSnapshot.Empty;
            lastLoggedObservedCount = -1;
            lastLoggedUnmappedCount = -1;
        }
    }

    private static void Register(Registration registration)
    {
        if (registration == null) return;
        if (registration.State == DevUiMigrationState.Unmapped)
            throw new ArgumentException("Leave unsupported types unregistered instead of registering Unmapped.");

        lock (Gate)
        {
            for (int i = Registrations.Count - 1; i >= 0; i--)
            {
                Registration existing = Registrations[i];
                bool sameType = registration.Type != null && existing.Type == registration.Type;
                bool sameName = registration.Type == null && existing.Type == null &&
                                string.Equals(existing.FullTypeName, registration.FullTypeName, StringComparison.Ordinal);
                if ((!sameType && !sameName) || existing.IncludeDerived != registration.IncludeDerived) continue;
                Registrations.RemoveAt(i);
            }
            Registrations.Add(registration);
        }
    }

    private static void Walk(
        DevUINode node,
        string path,
        bool insidePlacedObjectRepresentation,
        string pageType,
        DevUiMigrationSource inheritedSource,
        Dictionary<string, MutableEntry> output)
    {
        if (node == null) return;

        bool insideRepresentation = insidePlacedObjectRepresentation || node is PlacedObjectRepresentation;
        DevUiMigrationSource localSource = ResolveContextSource(node, inheritedSource);

        if (IsObligation(node))
            AddObservation(node, path, insideRepresentation, pageType, localSource, output);

        // Atomic controls own presentation-only children such as slider nubs and labels. Composite
        // Buttons are deliberately not atomic: when Clicked() opens a custom panel its descendants
        // must be scanned and tested by the same protocol rules.
        if (LegacyDevInterfaceBridge.IsAtomicAdaptedControl(node)) return;
        if (node.subNodes == null) return;

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            DevUINode child = node.subNodes[i];
            if (child == null) continue;
            Walk(child, path + "." + i, insideRepresentation, pageType, localSource, output);
        }
    }

    private static bool IsObligation(DevUINode node)
    {
        if (node == null || node is DevUILabel) return false;
        if (node is Page) return true;
        if (LegacyDevInterfaceBridge.CanAdaptNode(node)) return true;
        if (node is Handle) return true;
        if (MatchesAnyRegistration(node.GetType())) return true;
        return LooksInteractiveByReflection(node.GetType());
    }

    private static bool LooksInteractiveByReflection(Type type)
    {
        if (type == null) return false;
        // These method shapes are common custom DevInterface interaction boundaries. If a new mod
        // invents one that our semantic bridge cannot drive, it appears as Unmapped instead of
        // silently disappearing from the audit.
        return HasDeclaredMethod(type, "Clicked", Type.EmptyTypes) ||
               HasDeclaredMethod(type, "NubDragged", new[] { typeof(float) }) ||
               HasDeclaredMethod(type, "NubDragged2", new[] { typeof(float) }) ||
               HasDeclaredMethod(type, "Increment", new[] { typeof(int) }) ||
               HasDeclaredMethod(type, "TrySetValue", new[] { typeof(string), typeof(bool) });
    }

    private static bool HasDeclaredMethod(Type type, string name, Type[] parameters)
    {
        Type current = type;
        while (current != null && current != typeof(DevUINode))
        {
            MethodInfo method = current.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null,
                parameters,
                null);
            if (method != null) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static void AddObservation(
        DevUINode node,
        string path,
        bool insideRepresentation,
        string pageType,
        DevUiMigrationSource source,
        Dictionary<string, MutableEntry> output)
    {
        Type type = node.GetType();
        string context = insideRepresentation ? "PlacedObject" : "Page";
        ResolveMigration(type, node, out DevUiMigrationState state, out string note);
        string key = pageType + "|" + context + "|" + source + "|" +
                     (type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

        if (output.TryGetValue(key, out MutableEntry existing))
        {
            existing.InstanceCount++;
            if (state == DevUiMigrationState.Unmapped)
            {
                existing.State = state;
                existing.AdapterNote = note ?? string.Empty;
            }
            return;
        }

        output[key] = new MutableEntry
        {
            PageType = pageType,
            Context = context,
            NodeType = type,
            ExamplePath = path ?? string.Empty,
            ExampleId = node.IDstring ?? string.Empty,
            Source = source,
            State = state,
            AdapterNote = note ?? string.Empty,
            InstanceCount = 1
        };
    }

    private static void ResolveMigration(
        Type type,
        DevUINode node,
        out DevUiMigrationState state,
        out string note)
    {
        lock (Gate)
        {
            for (int i = Registrations.Count - 1; i >= 0; i--)
            {
                Registration registration = Registrations[i];
                if (!Matches(registration, type)) continue;
                state = registration.State;
                note = registration.Note;
                return;
            }
        }

        if (LegacyDevInterfaceBridge.CanAdaptNode(node))
        {
            state = DevUiMigrationState.GenericAdapter;
            note = GenericNote(node);
            return;
        }

        if (node is Handle)
        {
            state = DevUiMigrationState.Unmapped;
            note = "Scene-handle protocol is not yet mirrored by the generic ImGui bridge";
            return;
        }

        state = DevUiMigrationState.Unmapped;
        note = "Unknown interactive DevInterface protocol; add one structural capability adapter, not a per-mod type adapter";
    }

    private static string GenericNote(DevUINode node)
    {
        if (node is Slider) return "Generic Slider/NubDragged semantic bridge";
        if (node is Cycler) return "Generic Cycler semantic bridge";
        if (node is IntegerControl) return "Generic IntegerControl semantic bridge";
        if (node is ButtonWithSelectPanel) return "Generic select/button semantic bridge";
        if (LegacyDevInterfaceBridge.CanAdaptBoolean(node)) return "actualValue<bool> protocol";
        if (LegacyDevInterfaceBridge.CanAdaptExtEnum(node)) return "ExtEnum-valued button protocol";
        if (LegacyDevInterfaceBridge.CanAdaptPanelSelect(node)) return "actualValue<string> + values protocol";
        if (LegacyDevInterfaceBridge.CanAdaptColorSelect(node)) return "actualValue<Color> protocol";
        if (LegacyDevInterfaceBridge.CanAdaptText(node)) return "TrySetValue(string,bool) protocol";
        if (LegacyDevInterfaceBridge.CanAdaptDirection(node)) return "Dir<Vector2> protocol";
        if (node is Button) return "Generic Button.Clicked semantic bridge; child panels are recursively mirrored";
        return "Generic DevInterface semantic bridge";
    }

    private static bool MatchesAnyRegistration(Type actual)
    {
        lock (Gate)
        {
            for (int i = Registrations.Count - 1; i >= 0; i--)
                if (Matches(Registrations[i], actual)) return true;
        }
        return false;
    }

    private static bool Matches(Registration registration, Type actual)
    {
        if (registration == null || actual == null) return false;
        if (registration.Type != null)
            return registration.IncludeDerived ? registration.Type.IsAssignableFrom(actual) : registration.Type == actual;
        return string.Equals(registration.FullTypeName, actual.FullName, StringComparison.Ordinal);
    }

    private static DevUiMigrationSource ResolveContextSource(DevUINode node, DevUiMigrationSource inherited)
    {
        if (node == null) return inherited;

        // A custom representation/panel is the strongest ownership signal. Its vanilla Button and
        // Slider descendants should remain attributed to that mod rather than Assembly-CSharp.
        DevUiMigrationSource own = ResolveSource(node.GetType());
        if (own == DevUiMigrationSource.RegionKit || own == DevUiMigrationSource.DryCycle || own == DevUiMigrationSource.Other)
            return own;

        if (node is PlacedObjectRepresentation representation)
        {
            DevUiMigrationSource dataSource = ResolveSource(representation.pObj?.data?.GetType());
            if (dataSource != DevUiMigrationSource.Vanilla) return dataSource;
        }

        return inherited;
    }

    private static DevUiMigrationSource ResolveSource(Type type)
    {
        if (type == null) return DevUiMigrationSource.Vanilla;
        string ns = type.Namespace ?? string.Empty;
        string assembly = type.Assembly?.GetName().Name ?? string.Empty;

        if (StartsWith(ns, "DryCycle") || Contains(assembly, "DryCycle"))
            return DevUiMigrationSource.DryCycle;
        if (StartsWith(ns, "RegionKit") || Contains(assembly, "RegionKit"))
            return DevUiMigrationSource.RegionKit;

        if ((type.Assembly == typeof(DevUINode).Assembly && StartsWith(ns, "DevInterface")) ||
            (StartsWith(ns, "DevInterface") && Contains(assembly, "Assembly-CSharp")))
            return DevUiMigrationSource.Vanilla;

        // POM is the generic backend used by RegionKit and other mods; keep it in Other unless a
        // custom parent representation already established stronger ownership.
        return DevUiMigrationSource.Other;
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string needle) =>
        value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static MutableEntry Clone(MutableEntry source) => new()
    {
        PageType = source.PageType,
        Context = source.Context,
        NodeType = source.NodeType,
        ExamplePath = source.ExamplePath,
        ExampleId = source.ExampleId,
        Source = source.Source,
        State = source.State,
        AdapterNote = source.AdapterNote,
        InstanceCount = source.InstanceCount
    };

    private static DevUiMigrationCoverageSnapshot BuildSnapshot(string pageType, ICollection<MutableEntry> source)
    {
        List<DevUiMigrationCoverageEntry> entries = new(source?.Count ?? 0);
        if (source != null)
        {
            foreach (MutableEntry item in source)
            {
                Type type = item.NodeType;
                entries.Add(new DevUiMigrationCoverageEntry
                {
                    PageType = item.PageType ?? string.Empty,
                    Context = item.Context ?? string.Empty,
                    TypeName = type?.FullName ?? type?.Name ?? string.Empty,
                    Namespace = type?.Namespace ?? string.Empty,
                    AssemblyName = type?.Assembly?.GetName().Name ?? string.Empty,
                    ExamplePath = item.ExamplePath ?? string.Empty,
                    ExampleId = item.ExampleId ?? string.Empty,
                    AdapterNote = item.AdapterNote ?? string.Empty,
                    Source = item.Source,
                    State = item.State,
                    InstanceCount = item.InstanceCount
                });
            }
        }

        entries.Sort(CompareEntries);
        DevUiMigrationCoverageEntry[] array = entries.ToArray();
        return new DevUiMigrationCoverageSnapshot
        {
            PageType = pageType ?? string.Empty,
            Entries = array,
            Vanilla = BuildSummary(array, DevUiMigrationSource.Vanilla),
            RegionKit = BuildSummary(array, DevUiMigrationSource.RegionKit),
            DryCycle = BuildSummary(array, DevUiMigrationSource.DryCycle),
            Other = BuildSummary(array, DevUiMigrationSource.Other)
        };
    }

    private static int CompareEntries(DevUiMigrationCoverageEntry a, DevUiMigrationCoverageEntry b)
    {
        int source = a.Source.CompareTo(b.Source);
        if (source != 0) return source;
        int state = a.State.CompareTo(b.State);
        if (state != 0) return state;
        int page = string.Compare(a.PageType, b.PageType, StringComparison.Ordinal);
        if (page != 0) return page;
        int context = string.Compare(a.Context, b.Context, StringComparison.Ordinal);
        if (context != 0) return context;
        return string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
    }

    private static DevUiMigrationSourceSummary BuildSummary(
        DevUiMigrationCoverageEntry[] entries,
        DevUiMigrationSource source)
    {
        int total = 0;
        int mapped = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            DevUiMigrationCoverageEntry entry = entries[i];
            if (entry == null || entry.Source != source) continue;
            total++;
            if (entry.State != DevUiMigrationState.Unmapped) mapped++;
        }

        return new DevUiMigrationSourceSummary
        {
            Source = source,
            TotalTypeCount = total,
            MappedTypeCount = mapped
        };
    }

    private static void LogCoverageGrowthIfNeeded(DevUiMigrationCoverageSnapshot snapshot)
    {
        int count = snapshot?.TotalTypeCount ?? 0;
        int unmapped = snapshot?.UnmappedTypeCount ?? 0;
        if (count == lastLoggedObservedCount && unmapped == lastLoggedUnmappedCount) return;
        lastLoggedObservedCount = count;
        lastLoggedUnmappedCount = unmapped;

        DevUiMigrationSourceSummary vanilla = snapshot?.Vanilla;
        DevUiMigrationSourceSummary rk = snapshot?.RegionKit;
        DevUiMigrationSourceSummary dry = snapshot?.DryCycle;
        DevUiMigrationSourceSummary other = snapshot?.Other;
        Plugin.Logger?.LogInfo(
            "DevTool generic compatibility audit: " + count + " interactive protocols observed; unmapped: " +
            "Vanilla=" + (vanilla?.UnmappedTypeCount ?? 0) + ", " +
            "RegionKit=" + (rk?.UnmappedTypeCount ?? 0) + ", " +
            "DryCycle=" + (dry?.UnmappedTypeCount ?? 0) + ", " +
            "Other=" + (other?.UnmappedTypeCount ?? 0) + ".");
    }
}
