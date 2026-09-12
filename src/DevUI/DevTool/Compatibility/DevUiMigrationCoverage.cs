using System;
using System.Collections.Generic;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Origin of a legacy DevInterface node observed by the migration coverage scanner.
/// Source detection is intentionally reflection/name based so RegionKit remains optional.
/// </summary>
public enum DevUiMigrationSource
{
    Vanilla,
    RegionKit,
    DryCycle,
    Other
}

/// <summary>
/// How a legacy DevInterface obligation is represented by the rebuilt editor.
/// Unmapped is deliberately the default: coverage must be proven rather than assumed.
/// </summary>
public enum DevUiMigrationState
{
    NativeNewUi,
    GenericAdapter,
    SpecializedAdapter,
    Unmapped
}

/// <summary>
/// Immutable coverage row. One row represents a node type in one page/context bucket,
/// not one frame. InstanceCount is the maximum simultaneously observed instance count.
/// </summary>
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

/// <summary>
/// Detached immutable snapshot safe for the optional RWImGui frontend to read.
/// CurrentPage contains the live page; Observed is the cumulative process/session catalog.
/// </summary>
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

    public DevUiMigrationSourceSummary Summary(DevUiMigrationSource source)
    {
        return source switch
        {
            DevUiMigrationSource.Vanilla => Vanilla,
            DevUiMigrationSource.RegionKit => RegionKit,
            DevUiMigrationSource.DryCycle => DryCycle,
            _ => Other
        };
    }

    private static DevUiMigrationSourceSummary EmptySummary(DevUiMigrationSource source) => new()
    {
        Source = source,
        TotalTypeCount = 0,
        MappedTypeCount = 0
    };
}

/// <summary>
/// Runtime migration guard for vanilla, RegionKit and DryCycle DevInterface trees.
///
/// The scanner walks the real active legacy tree after DevInterface has updated. It does not
/// require a RegionKit reference and therefore also catches controls injected dynamically by
/// any optional mod. Unknown controls are always Unmapped until an adapter explicitly proves
/// otherwise.
///
/// Coverage is context-sensitive enough to avoid a dangerous false positive: the generic
/// Button/Slider bridge currently exists only inside PlacedObjectRepresentation inspectors, so
/// the same Button/Slider type elsewhere on a page remains Unmapped.
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

    static DevUiMigrationCoverage()
    {
        // Top-level pages already have dedicated rebuilt workspaces. Their children still need
        // independent coverage and are NOT made mapped by this registration.
        RegisterExact(typeof(RoomSettingsPage), DevUiMigrationState.NativeNewUi, "Rebuilt Room workspace");
        RegisterExact(typeof(ObjectsPage), DevUiMigrationState.NativeNewUi, "Rebuilt Objects workspace");
        RegisterExact(typeof(SoundPage), DevUiMigrationState.NativeNewUi, "Rebuilt Sound workspace");
        RegisterExact(typeof(TriggersPage), DevUiMigrationState.NativeNewUi, "Rebuilt Triggers workspace");
        RegisterExact(typeof(MapPage), DevUiMigrationState.NativeNewUi, "Rebuilt Map workspace");
        RegisterExact(typeof(DialogPage), DevUiMigrationState.NativeNewUi, "Rebuilt Dialog workspace");
        RegisterExact(typeof(RelationshipPage), DevUiMigrationState.NativeNewUi, "Rebuilt Relationships workspace");
    }

    /// <summary>Coverage of the active legacy page from the latest DevUI update.</summary>
    public static DevUiMigrationCoverageSnapshot CurrentPage => currentPage;

    /// <summary>
    /// Cumulative set of page/context/type obligations seen since the DevTool runtime started.
    /// Opening more legacy pages/modules expands this catalog; repeated frames do not inflate it.
    /// </summary>
    public static DevUiMigrationCoverageSnapshot Observed => observed;

    /// <summary>Register an exact legacy node type as handled by a known migration path.</summary>
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

    /// <summary>
    /// Register a base node type and all derived types. Use this only when the adapter genuinely
    /// handles arbitrary subclasses; otherwise prefer RegisterExact to avoid hiding migration gaps.
    /// </summary>
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

    /// <summary>
    /// Register an optional third-party node without referencing its assembly at compile time.
    /// The full CLR type name is matched exactly.
    /// </summary>
    public static void RegisterTypeName(string fullTypeName, DevUiMigrationState state, string note = null)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName)) throw new ArgumentException("Type name is required.", nameof(fullTypeName));
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
            Walk(page, "root", false, page.GetType().FullName ?? page.GetType().Name, pageEntries);

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
                    }
                    else
                    {
                        ObservedEntries[pair.Key] = Clone(incoming);
                    }
                }

                currentPage = BuildSnapshot(page.GetType().FullName ?? page.GetType().Name, pageEntries.Values);
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
        }
    }

    private static void Register(Registration registration)
    {
        if (registration == null) return;
        if (registration.State == DevUiMigrationState.Unmapped)
            throw new ArgumentException("Registering Unmapped is not useful; simply leave the node unregistered.");

        lock (Gate)
        {
            for (int i = Registrations.Count - 1; i >= 0; i--)
            {
                Registration existing = Registrations[i];
                bool sameType = registration.Type != null && existing.Type == registration.Type;
                bool sameName = registration.Type == null && existing.Type == null &&
                                string.Equals(existing.FullTypeName, registration.FullTypeName, StringComparison.Ordinal);
                if (!sameType && !sameName) continue;
                if (existing.IncludeDerived != registration.IncludeDerived) continue;
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
        Dictionary<string, MutableEntry> output)
    {
        if (node == null) return;

        bool insideRepresentation = insidePlacedObjectRepresentation || node is PlacedObjectRepresentation;
        Type nodeType = node.GetType();

        // Exact stock labels are presentation fragments of their parent control, not independent
        // editor capabilities. Derived label types are kept because mods often add behavior there.
        bool stockLabelFragment = nodeType == typeof(DevUILabel);
        if (!stockLabelFragment)
            AddObservation(node, path, insideRepresentation, pageType, output);

        // Slider owns title/number labels, optional inherit button and SliderNub. Treat the slider
        // as one migration obligation so its implementation details cannot distort coverage.
        if (node is Slider) return;
        if (node.subNodes == null) return;

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            DevUINode child = node.subNodes[i];
            if (child == null) continue;
            Walk(child, path + "." + i, insideRepresentation, pageType, output);
        }
    }

    private static void AddObservation(
        DevUINode node,
        string path,
        bool insideRepresentation,
        string pageType,
        Dictionary<string, MutableEntry> output)
    {
        Type type = node.GetType();
        string context = insideRepresentation ? "PlacedObject" : "Page";
        ResolveMigration(type, node, insideRepresentation, out DevUiMigrationState state, out string note);
        DevUiMigrationSource source = ResolveSource(type);
        string key = pageType + "|" + context + "|" + (type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

        if (output.TryGetValue(key, out MutableEntry existing))
        {
            existing.InstanceCount++;
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
        bool insideRepresentation,
        out DevUiMigrationState state,
        out string note)
    {
        // Page-level navigation/save infrastructure is already replaced by rebuilt navigation and
        // the shared command row. Match by concrete role/ID rather than declaring every Button safe.
        if (IsRebuiltPageInfrastructure(node))
        {
            state = DevUiMigrationState.NativeNewUi;
            note = "Rebuilt page navigation/command infrastructure";
            return;
        }

        // The existing object compatibility bridge recursively exposes arbitrary Button/Slider
        // subclasses under the selected PlacedObjectRepresentation and delegates mutations back
        // through Clicked/NubDragged. Do not apply this rule anywhere else.
        if (insideRepresentation && node is Slider)
        {
            state = DevUiMigrationState.GenericAdapter;
            note = "PlacedObject legacy Slider bridge";
            return;
        }
        if (insideRepresentation && node is Button)
        {
            state = DevUiMigrationState.GenericAdapter;
            note = "PlacedObject legacy Button bridge";
            return;
        }

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

        state = DevUiMigrationState.Unmapped;
        note = string.Empty;
    }

    private static bool IsRebuiltPageInfrastructure(DevUINode node)
    {
        if (node is SwitchPageButton) return true;
        if (node is not Button button) return false;

        string id = button.IDstring ?? string.Empty;
        return id == "Save_Settings" || id == "Save_Specific" || id == "Export_Sandbox" ||
               id == "Prev_Button" || id == "Next_Button";
    }

    private static bool Matches(Registration registration, Type actual)
    {
        if (registration == null || actual == null) return false;
        if (registration.Type != null)
            return registration.IncludeDerived ? registration.Type.IsAssignableFrom(actual) : registration.Type == actual;
        return string.Equals(registration.FullTypeName, actual.FullName, StringComparison.Ordinal);
    }

    private static DevUiMigrationSource ResolveSource(Type type)
    {
        string ns = type?.Namespace ?? string.Empty;
        string assembly = type?.Assembly?.GetName().Name ?? string.Empty;

        if (StartsWith(ns, "DryCycle") || Contains(assembly, "DryCycle"))
            return DevUiMigrationSource.DryCycle;
        if (StartsWith(ns, "RegionKit") || Contains(assembly, "RegionKit"))
            return DevUiMigrationSource.RegionKit;

        // At runtime vanilla DevInterface can live in Assembly-CSharp or a publicized variant.
        // Comparing the declaring assembly of DevUINode also survives those packaging differences.
        if ((type != null && type.Assembly == typeof(DevUINode).Assembly && StartsWith(ns, "DevInterface")) ||
            (StartsWith(ns, "DevInterface") && Contains(assembly, "Assembly-CSharp")))
            return DevUiMigrationSource.Vanilla;

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
        if (count == lastLoggedObservedCount) return;
        lastLoggedObservedCount = count;

        DevUiMigrationSourceSummary vanilla = snapshot?.Vanilla;
        DevUiMigrationSourceSummary rk = snapshot?.RegionKit;
        DevUiMigrationSourceSummary dry = snapshot?.DryCycle;
        DevUiMigrationSourceSummary other = snapshot?.Other;
        Plugin.Logger?.LogInfo(
            "DevTool migration coverage observed " + count + " obligations; unmapped: " +
            "Vanilla=" + (vanilla?.UnmappedTypeCount ?? 0) + ", " +
            "RegionKit=" + (rk?.UnmappedTypeCount ?? 0) + ", " +
            "DryCycle=" + (dry?.UnmappedTypeCount ?? 0) + ", " +
            "Other=" + (other?.UnmappedTypeCount ?? 0) + ".");
    }
}
