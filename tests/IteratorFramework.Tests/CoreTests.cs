using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DryCycle.Iterators;

namespace IteratorFramework.Tests;

internal static class CoreTests
{
    private static int _assertions;

    internal static int Run()
    {
        var tests = new KeyValuePair<string, Action>[]
        {
            Test("task-book example and game ID mapping", MinimalExample),
            Test("ID parsing, value equality and validation", IdentifierContract),
            Test("immutable descriptor and builder snapshots", ImmutableDefinitions),
            Test("builder batch validation is atomic", BuilderValidation),
            Test("duplicate and room-conflict registration is atomic", RegistrationConflicts),
            Test("registration order and stable read-only snapshots", RegistrySnapshots),
            Test("vanilla and foreign Oracle IDs remain owned by their providers", OracleOwnership),
            Test("unregister, reload and stale registration ownership", ReloadOwnership),
            Test("Oracle mapping survives ExtEnum index changes", OracleIndexChanges),
            Test("null and invalid inputs fail predictably", InvalidInputs),
            Test("scoped logging and failing log backends", LoggingIsolation),
            Test("external descriptor construction and builder extension", ExternalConsumer),
            Test("repeated registration lifecycle leaves no residue", RepeatedLifecycle)
        };

        int failures = 0;
        foreach (KeyValuePair<string, Action> test in tests)
        {
            try
            {
                Check(IteratorRegistry.Registered.Count == 0, "test begins with an empty registry");
                test.Value();
                Console.WriteLine("PASS " + test.Key);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + test.Key + Environment.NewLine + exception);
            }
            finally
            {
                // Only this standalone process owns these test registrations. Iterating a
                // snapshot also verifies that mutation cannot invalidate enumeration.
                foreach (IteratorDescriptor descriptor in IteratorRegistry.Registered)
                    IteratorRegistry.Unregister(descriptor);
            }
        }

        Console.WriteLine($"Iterator Framework phase 1: {tests.Length - failures}/{tests.Length} groups passed; {_assertions} assertions; {failures} failures.");
        Console.WriteLine("Executed against the built DryCycle.dll and installed managed Assembly-CSharp.dll; no Unity room/runtime test was run.");
        return failures == 0 ? 0 : 1;
    }

    private static KeyValuePair<string, Action> Test(string name, Action action) => new(name, action);

    private static void MinimalExample()
    {
        IteratorDescriptor descriptor = Iterator.Create("TEST")
            .Name("Test Iterator")
            .Room("TEST_AI")
            .Register();

        Check(descriptor.DisplayName == "Test Iterator", "example display name");
        Check(IteratorRegistry.TryGet(new IteratorID("TEST"), out var byID) && ReferenceEquals(byID, descriptor), "ID lookup");
        Check(IteratorRegistry.TryGet("TEST", out var byString) && ReferenceEquals(byString, descriptor), "string lookup");
        Check(IteratorRegistry.TryGetByRoom("test_ai", out var byRoom) && ReferenceEquals(byRoom, descriptor), "case-insensitive room lookup");
        Check(!IteratorRegistry.TryGet("test", out _), "IDs remain case-sensitive");
        Check(IteratorID.TryGet("TEST", out var id) && ReferenceEquals(id, descriptor.ID), "registered ID lookup");
        Check(IteratorID.IsRegistered("TEST"), "registered status");
        Check(IteratorRegistry.TryGetOracleID(id, out var gameID) && gameID.Index >= 0 && gameID.value == "TEST", "real ExtEnum registration");
        Check(IteratorRegistry.TryGetByOracleID(new Oracle.OracleID("TEST", false), out var mapped) && ReferenceEquals(mapped, descriptor), "value-based game ID mapping");
        Check(!IteratorRegistry.TryGetByOracleID(Oracle.OracleID.SS, out _), "vanilla IDs never resolve to the framework");
        Check(!IteratorRegistry.TryGetByRoom("UNREGISTERED_AI", out _), "no suffix inference");
        IteratorDescriptor ordinaryRoom = Iterator.Create("ORDINARY").Room("TEST_CHAMBER").Register();
        Check(ordinaryRoom.DisplayName == "ORDINARY", "default display name");
        Check(IteratorRegistry.TryGetByRoom("TEST_CHAMBER", out _), "no mandatory AI suffix");
    }

    private static void IdentifierContract()
    {
        var first = new IteratorID("Mod.Test-01");
        var second = IteratorID.Parse("Mod.Test-01");
        Check(first == second && first.Equals((object)second), "ID value equality");
        Check(first.GetHashCode() == second.GetHashCode(), "equal hash codes");
        Check(first != new IteratorID("mod.Test-01"), "case-sensitive equality");
        Check(first.ToString() == "Mod.Test-01", "string conversion");
        Check(first != null && !(first == null) && (IteratorID)null == null, "null equality");
        Check(new HashSet<IteratorID> { first }.Contains(second), "ID dictionary/hash-set contract");
        Check(!IteratorID.IsRegistered(first.Value) && !IteratorID.TryGet(first.Value, out _), "parsing does not register");
        Check(new Oracle.OracleID(first.Value, false).Index == -1, "parsing does not allocate an ExtEnum entry");
        foreach (string bad in new[] { null, "", " ", " A", "A ", "A B", "A\nB", "A/B", "A\\B", "A:B", "-A", ".A", "测试", new string('A', 129) })
        {
            Check(!IteratorID.TryParse(bad, out var parsed) && parsed == null, "TryParse rejects invalid ID");
            if (bad != null)
                Throws<FormatException>(() => IteratorID.Parse(bad));
        }
        Throws<ArgumentNullException>(() => IteratorID.Parse(null));
        Throws<ArgumentNullException>(() => new IteratorID(null));
        Throws<ArgumentException>(() => new IteratorID("BAD ID"));
        Check(IteratorID.TryParse(new string('A', 128), out _), "maximum ID length");
        Check(IteratorID.TryParse("_1", out _), "underscore and digit support");
    }

    private static void ImmutableDefinitions()
    {
        var rooms = new List<string> { "IMMUTABLE_ROOM" };
        var metadata = new Dictionary<string, string> { ["Example.Key"] = "original" };
        var descriptor = new IteratorDescriptor(new IteratorID("IMMUTABLE"), rooms, "不可变定义", metadata);
        rooms[0] = "CHANGED_ROOM";
        metadata["Example.Key"] = "changed";
        metadata["Example.New"] = "new";
        Check(descriptor.Rooms[0] == "IMMUTABLE_ROOM", "input room collection is copied");
        Check(descriptor.Metadata.Count == 1 && descriptor.Metadata["Example.Key"] == "original", "metadata copied deeply enough for string values");
        Throws<NotSupportedException>(() => ((IList<string>)descriptor.Rooms)[0] = "MUTATION");
        Throws<NotSupportedException>(() => ((IDictionary<string, string>)descriptor.Metadata).Add("Mutation", "value"));

        IteratorBuilder builder = Iterator.Create("BUILDER").Room("BUILDER_ROOM").WithMetadata("Example.Key", "old");
        IteratorDescriptor built = builder.Build();
        IteratorRegistry.Register(built);
        builder.Room("BUILDER_EXTRA").Name("New name").WithMetadata("Example.Key", "new");
        IteratorDescriptor later = builder.Build();
        Check(built.Rooms.Count == 1 && later.Rooms.Count == 2, "Build creates independent room snapshots");
        Check(built.DisplayName == "BUILDER" && later.DisplayName == "New name", "name changes cannot alter registered descriptor");
        Check(built.Metadata["Example.Key"] == "old" && later.Metadata["Example.Key"] == "new", "builder metadata changes do not leak");
        Check(!IteratorRegistry.TryGetByRoom("BUILDER_EXTRA", out _), "builder cannot silently change registry indexes");
        descriptor.Validate();
        descriptor.Validate();
        Check(!IteratorID.IsRegistered("IMMUTABLE"), "Validate has no registration side effects");
        Check(new Oracle.OracleID("IMMUTABLE", false).Index == -1, "constructing a descriptor has no game ID side effects");
    }

    private static void BuilderValidation()
    {
        IteratorBuilder builder = Iterator.Create("BATCH").Room("BATCH_ORIGINAL");
        Throws<ArgumentException>(() => builder.Rooms("BATCH_EXTRA", "batch_original"));
        Check(builder.Build().Rooms.Count == 1, "duplicate late in a batch leaves no early additions");
        Throws<ArgumentException>(() => builder.Rooms("BATCH_EXTRA", "bad room"));
        Check(builder.Build().Rooms.Count == 1, "invalid late room leaves no early additions");
        Throws<ArgumentNullException>(() => builder.Rooms("BATCH_EXTRA", null));
        Throws<ArgumentNullException>(() => builder.Rooms(null));
        Throws<ArgumentException>(() => builder.Rooms());
        Throws<ArgumentException>(() => builder.Rooms("X", "x"));
        Check(builder.Build().Rooms.Count == 1, "all rejected batches preserve builder");
        builder.Rooms("BATCH_EXTRA", "BATCH_LAST");
        Check(builder.Build().Rooms.Count == 3, "valid batch commits completely");
        Throws<ArgumentException>(() => Iterator.Create("NO_ROOM").Build());
        Throws<ArgumentException>(() => Iterator.Create("NO_ROOM").Register());
        Check(!IteratorID.IsRegistered("NO_ROOM"), "missing room is rejected before registry");
    }

    private static void RegistrationConflicts()
    {
        IteratorDescriptor original = Iterator.Create("OWNER").Room("SHARED_ROOM").Register();
        var snapshot = IteratorRegistry.Registered;
        Check(ReferenceEquals(original, IteratorRegistry.Register(original)), "same descriptor registration is idempotent");
        Check(ReferenceEquals(snapshot, IteratorRegistry.Registered), "idempotent register does not republish state");
        Throws<InvalidOperationException>(() => Iterator.Create("OWNER").Room("FREE_ROOM").Register(), "OWNER");
        Check(!IteratorRegistry.TryGetByRoom("FREE_ROOM", out _), "duplicate ID cannot reserve new rooms");
        Throws<InvalidOperationException>(() => Iterator.Create("CONFLICT").Rooms("FREE_ROOM", "shared_room").Register(), "SHARED", true);
        Check(!IteratorID.IsRegistered("CONFLICT"), "conflicting registration does not publish ID");
        Check(new Oracle.OracleID("CONFLICT", false).Index == -1, "conflicting registration does not allocate Oracle ID");
        Check(!IteratorRegistry.TryGetByRoom("FREE_ROOM", out _), "late room conflict leaves earlier room free");
        Check(ReferenceEquals(snapshot, IteratorRegistry.Registered), "failed registrations leave exact previous snapshot");
        IteratorDescriptor recovered = Iterator.Create("CONFLICT").Room("FREE_ROOM").Register();
        Check(IteratorRegistry.TryGetByRoom("FREE_ROOM", out var found) && ReferenceEquals(recovered, found), "retry after conflict succeeds");
        Check(IteratorRegistry.TryGetByRoom("SHARED_ROOM", out found) && ReferenceEquals(original, found), "original binding remains intact");
    }

    private static void RegistrySnapshots()
    {
        var emptySnapshot = IteratorRegistry.Registered;
        IteratorDescriptor first = Iterator.Create("ORDER_FIRST").Room("ORDER_FIRST_ROOM").Register();
        var oneSnapshot = IteratorRegistry.Registered;
        IteratorDescriptor second = Iterator.Create("ORDER_SECOND").Room("ORDER_SECOND_ROOM").Register();
        var twoSnapshot = IteratorRegistry.Registered;
        Check(emptySnapshot.Count == 0 && oneSnapshot.Count == 1 && twoSnapshot.Count == 2, "old snapshots never grow");
        Check(ReferenceEquals(twoSnapshot[0], first) && ReferenceEquals(twoSnapshot[1], second), "declaration order retained");
        Throws<NotSupportedException>(() => ((IList<IteratorDescriptor>)twoSnapshot).Clear());
        Check(IteratorRegistry.Unregister(first), "unregister first item");
        Check(twoSnapshot.Count == 2 && ReferenceEquals(twoSnapshot[0], first), "old snapshot does not shrink");
        Check(IteratorRegistry.Registered.Count == 1 && ReferenceEquals(IteratorRegistry.Registered[0], second), "new snapshot reflects removal");
        IteratorRegistry.Register(first);
        Check(ReferenceEquals(IteratorRegistry.Registered[1], first), "re-registration appends after retained registrations");
    }

    private static void OracleOwnership()
    {
        Oracle.OracleID pebbles = Oracle.OracleID.SS;
        Oracle.OracleID moon = Oracle.OracleID.SL;
        int pebblesIndex = pebbles.Index;
        int moonIndex = moon.Index;
        foreach (string reserved in new[] { "SS", "SL", "SS_Cutscene", "SL_Cutscene", "ST_Cutscene", "DM", "ST", "CL" })
        {
            int beforeIndex = new Oracle.OracleID(reserved, false).Index;
            Throws<InvalidOperationException>(() => Iterator.Create(reserved).Room("RESERVED_ROOM").Register(), "reserved");
            Check(new Oracle.OracleID(reserved, false).Index == beforeIndex, "rejected reserved ID leaves game registration unchanged");
            Check(!IteratorID.IsRegistered(reserved), "reserved ID never enters framework");
        }
        var foreign = new Oracle.OracleID("FOREIGN_MOD", true);
        try
        {
            IteratorDescriptor rejected = Iterator.Create("FOREIGN_MOD").Room("FOREIGN_ROOM").Build();
            Throws<InvalidOperationException>(() => IteratorRegistry.Register(rejected), "outside");
            Check(!IteratorRegistry.Unregister(rejected), "unregistering an unowned definition is harmless");
            Check(foreign.Index >= 0, "foreign provider retains its ExtEnum");
            Check(!IteratorRegistry.TryGetByOracleID(foreign, out _), "foreign identity does not resolve");
            Check(!IteratorRegistry.TryGetByRoom("FOREIGN_ROOM", out _), "foreign conflict leaves room free");
        }
        finally
        {
            foreign.Unregister();
        }
        Check(ReferenceEquals(pebbles, Oracle.OracleID.SS) && pebbles.Index == pebblesIndex, "Pebbles ID and index retained");
        Check(ReferenceEquals(moon, Oracle.OracleID.SL) && moon.Index == moonIndex, "Moon ID and index retained");
    }

    private static void ReloadOwnership()
    {
        IteratorDescriptor old = Iterator.Create("RELOAD").Rooms("RELOAD_A", "RELOAD_B").Register();
        Check(IteratorRegistry.TryGetOracleID(old.ID, out var oldGameID), "mapping before unregister");
        Check(IteratorRegistry.Unregister(old), "first unregister succeeds");
        Check(!IteratorRegistry.Unregister(old), "repeated unregister returns false");
        Check(!IteratorID.IsRegistered("RELOAD") && !IteratorRegistry.TryGetByRoom("RELOAD_B", out _), "all indexes released");
        Check(oldGameID.Index == -1, "owned ExtEnum is released");
        Check(!IteratorRegistry.TryGetOracleID(old.ID, out _), "unregistered ID has no mapping");
        IteratorDescriptor replacement = Iterator.Create("RELOAD").Name("Reloaded").Room("RELOAD_A").Register();
        Check(!IteratorRegistry.Unregister(old), "stale descriptor cannot remove replacement");
        Check(IteratorRegistry.TryGet("RELOAD", out var current) && ReferenceEquals(current, replacement), "replacement survives stale cleanup");
        Check(IteratorRegistry.TryGetOracleID(replacement.ID, out var first), "first returned game value");
        Check(IteratorRegistry.TryGetOracleID(replacement.ID, out var second) && !ReferenceEquals(first, second), "game values are detached objects");
        first.value = "CHANGED_BY_CALLER";
        Check(second.value == "RELOAD" && IteratorID.IsRegistered("RELOAD"), "mutable game object cannot mutate descriptor or index");
    }

    private static void OracleIndexChanges()
    {
        IteratorDescriptor first = Iterator.Create("INDEX_A").Room("INDEX_ROOM_A").Register();
        IteratorDescriptor second = Iterator.Create("INDEX_B").Room("INDEX_ROOM_B").Register();
        Check(IteratorRegistry.TryGetOracleID(second.ID, out var gameID), "get game ID before index shift");
        int oldIndex = gameID.Index;
        IteratorRegistry.Unregister(first);
        Check(gameID.Index == oldIndex - 1, "real ExtEnum recomputes shifted index");
        Check(IteratorRegistry.TryGetByOracleID(gameID, out var mapped) && ReferenceEquals(mapped, second), "mapping survives shifted index");
        Check(IteratorRegistry.TryGetByRoom("INDEX_ROOM_B", out mapped) && ReferenceEquals(mapped, second), "room index unaffected");
    }

    private static void InvalidInputs()
    {
        Throws<ArgumentNullException>(() => Iterator.Create((IteratorID)null));
        Throws<ArgumentNullException>(() => Iterator.Create((string)null));
        Throws<ArgumentNullException>(() => IteratorRegistry.Register(null));
        Throws<ArgumentNullException>(() => IteratorRegistry.Unregister(null));
        Throws<ArgumentNullException>(() => new IteratorDescriptor(null, new[] { "ROOM" }));
        Throws<ArgumentNullException>(() => new IteratorDescriptor(new IteratorID("VALID"), null));
        Throws<ArgumentException>(() => new IteratorDescriptor(new IteratorID("VALID"), new[] { "ROOM", "room" }));
        Throws<ArgumentException>(() => new IteratorDescriptor(new IteratorID("VALID"), new[] { "ROOM" }, " "));
        Throws<ArgumentNullException>(() => Iterator.Create("VALID").Name(null));
        Throws<ArgumentException>(() => Iterator.Create("VALID").Name("new\nline"));
        Throws<ArgumentNullException>(() => Iterator.Create("VALID").WithMetadata(null, "value"));
        Throws<ArgumentException>(() => Iterator.Create("VALID").WithMetadata(" ", "value"));
        Throws<ArgumentNullException>(() => Iterator.Create("VALID").WithMetadata("key", null));
        Throws<ArgumentException>(() => new IteratorDescriptor(new IteratorID("VALID"), new[] { "ROOM" }, metadata: new Dictionary<string, string> { ["key"] = null }));

        foreach (string absent in new[] { null, "", " ", "UNKNOWN", " ROOM " })
        {
            Check(!IteratorRegistry.TryGet(absent, out var found) && found == null, "absent ID returns false/null");
            Check(!IteratorRegistry.TryGetByRoom(absent, out found) && found == null, "absent room returns false/null");
            Check(!IteratorID.TryGet(absent, out var id) && id == null, "absent registered ID returns false/null");
            Check(!IteratorID.IsRegistered(absent), "absent IsRegistered returns false");
        }
        Check(!IteratorRegistry.TryGet((IteratorID)null, out var result) && result == null, "null value object lookup");
        Check(!IteratorRegistry.TryGetByOracleID(null, out result) && result == null, "null Oracle ID lookup");
        Check(!IteratorRegistry.TryGetOracleID(null, out _), "null ID conversion");
    }

    private static void LoggingIsolation()
    {
        var messages = new List<string>();
        var levels = new List<IteratorLogLevel>();
        var logger = new IteratorLogger(new IteratorID("LOG"), (level, message) => { levels.Add(level); messages.Add(message); });
        IteratorLogger scoped = logger.ForModule("Graphics").ForPhase("Initialize");
        scoped.Info("hello");
        scoped.Warn("recoverable");
        scoped.Error("failed", new InvalidOperationException("resource absent"));
        logger.Info(null);
        Check(levels.Count == 4 && levels[0] == IteratorLogLevel.Info && levels[1] == IteratorLogLevel.Warning && levels[2] == IteratorLogLevel.Error, "log levels preserved");
        Check(messages[0].Contains("[Iterator:LOG][Module:Graphics][Phase:Initialize]"), "automatic scope tags");
        Check(messages[2].Contains("InvalidOperationException") && messages[2].Contains("resource absent"), "exception details retained");
        Check(messages[3].Contains("[Module:Core][Phase:Registration]") && messages[3].Contains("<null>"), "derived context does not mutate original");
        Throws<ArgumentNullException>(() => new IteratorLogger(null));
        Throws<ArgumentException>(() => logger.ForModule(" "));
        Throws<ArgumentNullException>(() => logger.ForPhase(null));

        new IteratorLogger(new IteratorID("BROKEN_SINK"), (_, __) => throw new IOException("sink unavailable")).Error("must survive");
        Check(true, "failing sink does not propagate");
        int calls = 0;
        IteratorLogger recursive = null;
        recursive = new IteratorLogger(new IteratorID("RECURSIVE"), (_, __) => { calls++; recursive.Info("inner"); });
        recursive.Info("outer");
        Check(calls == 1, "recursive log backend is bounded");
        scoped.Error("invalid extension exception", new BrokenException());
        scoped.Info("after invalid exception");
        Check(messages[messages.Count - 1].Contains("after invalid exception"), "formatting failure releases recursion guard");

        var listener = new ThrowingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            IteratorDescriptor descriptor = Iterator.Create("TRACE_FAILURE").Room("TRACE_FAILURE_ROOM").Register();
            Check(IteratorID.IsRegistered("TRACE_FAILURE"), "default log failure cannot undo successful registration");
            Check(IteratorRegistry.Unregister(descriptor), "default log failure cannot block cleanup");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private static void ExternalConsumer()
    {
        var direct = new IteratorDescriptor(new IteratorID("DIRECT"), new[] { "DIRECT_ROOM" }, "Direct definition",
            new Dictionary<string, string> { ["Example.Author"] = "author" });
        direct.Validate();
        Check(ReferenceEquals(direct, IteratorRegistry.Register(direct)), "non-fluent public construction works");
        IteratorDescriptor extended = Iterator.Create("EXTENDED").UseExampleMetadata().Room("EXTENDED_ROOM").Register();
        Check(extended.Metadata["Example.Module"] == "enabled", "external extension method can compose public API");
    }

    private static void RepeatedLifecycle()
    {
        for (int cycle = 0; cycle < 40; cycle++)
        {
            var descriptors = new List<IteratorDescriptor>();
            for (int i = 0; i < 4; i++)
            {
                string id = "LOOP_" + i;
                IteratorDescriptor descriptor = Iterator.Create(id).Rooms(id + "_A", id + "_B").Register();
                descriptors.Add(descriptor);
                IteratorRegistry.Register(descriptor);
                Check(IteratorID.IsRegistered(id), "repeated registration lookup");
                Check(IteratorRegistry.TryGetByRoom(id + "_B", out _), "repeated room lookup");
                Check(new Oracle.OracleID(id, false).Index >= 0, "repeated game ID allocation");
            }
            Check(IteratorRegistry.Registered.Count == 4, "idempotence preserves registration count");
            foreach (IteratorDescriptor descriptor in descriptors)
            {
                Check(IteratorRegistry.Unregister(descriptor), "registration removed");
                Check(!IteratorRegistry.Unregister(descriptor), "repeated removal harmless");
                Check(!IteratorID.IsRegistered(descriptor.ID.Value), "registry ID released");
                Check(!IteratorRegistry.TryGetByRoom(descriptor.Rooms[0], out _), "room released");
                Check(new Oracle.OracleID(descriptor.ID.Value, false).Index == -1, "game ID released");
            }
            Check(IteratorRegistry.Registered.Count == 0, "complete lifecycle leaves empty registry");
        }
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static void Throws<T>(Action action, string contains = null, bool ignoreCase = false) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            Check(contains == null || exception.Message.IndexOf(contains, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0,
                "exception has actionable context: " + exception.Message);
            return;
        }
        throw new InvalidOperationException("Expected exception: " + typeof(T).Name);
    }

    private sealed class BrokenException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("broken exception formatter");
    }

    private sealed class ThrowingTraceListener : TraceListener
    {
        public override void Write(string message) => throw new IOException("broken Trace listener");
        public override void WriteLine(string message) => throw new IOException("broken Trace listener");
    }
}

internal static class ExampleExtension
{
    internal static IteratorBuilder UseExampleMetadata(this IteratorBuilder builder) => builder.WithMetadata("Example.Module", "enabled");
}
