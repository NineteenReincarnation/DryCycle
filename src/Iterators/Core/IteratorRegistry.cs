using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DryCycle.Iterators;

/// <summary>
/// 全局定义注册表。注册/注销必须在 Unity 主线程执行；不保存 Room、Oracle 实体或 Session。
/// 查询按字符串值建立索引，不依赖会随 ExtEnum 注销而变化的 Index。
/// </summary>
public static class IteratorRegistry
{
    private static Snapshot _snapshot = new(Array.Empty<IteratorDescriptor>());
    private static readonly HashSet<string> RetiringIDs = new(StringComparer.Ordinal);

    internal static bool IsRetiring(IteratorDescriptor descriptor) => RetiringIDs.Contains(descriptor.ID.Value);

    /// <summary>按注册顺序返回不可变快照；后续注册/注销不会改变已经取得的集合。</summary>
    public static IReadOnlyList<IteratorDescriptor> Registered => _snapshot.Descriptors;

    /// <summary>
    /// 先验证全部冲突，再发布完整索引。重复传入同一 Descriptor 幂等；同 ID 的另一份定义报错。
    /// 成功时登记对应的 Oracle.OracleID，但不会生成实体或安装 Hook。
    /// </summary>
    public static IteratorDescriptor Register(IteratorDescriptor descriptor)
    {
        if (descriptor == null)
            throw new ArgumentNullException(nameof(descriptor));

        var logger = new IteratorLogger(descriptor.ID).ForModule("Registry").ForPhase("Register");
        try
        {
            if (IsRetiring(descriptor))
                throw new InvalidOperationException($"IteratorFramework: iterator '{descriptor.ID}' is being unregistered; wait for its destruction callbacks to finish.");
            descriptor.Validate();
            Snapshot current = _snapshot;
            if (current.ByID.TryGetValue(descriptor.ID.Value, out IteratorDescriptor existing))
            {
                if (ReferenceEquals(existing, descriptor))
                    return descriptor;
                throw new InvalidOperationException(
                    $"IteratorFramework: iterator '{descriptor.ID}' is already registered as '{existing.DisplayName}'; " +
                    "a different descriptor cannot replace it. Unregister the original descriptor first.");
            }

            foreach (string room in descriptor.Rooms)
            {
                if (current.ByRoom.TryGetValue(room, out existing))
                    throw new InvalidOperationException(
                        $"IteratorFramework: iterator '{descriptor.ID}' cannot bind room '{room}'; " +
                        $"it is already bound to iterator '{existing.ID}'.");
            }

            IteratorOracleIds.ValidateAvailable(descriptor.ID);
            var entries = new List<IteratorDescriptor>(current.Descriptors) { descriptor };
            // Construct every collection before touching the game's global ExtEnum table.
            var next = new Snapshot(entries);
            IteratorOracleIds.Register(descriptor.ID);
            _snapshot = next;
        }
        catch (Exception exception)
        {
            logger.Error("Registration failed; the descriptor was not added.", exception);
            throw;
        }

        logger.Info($"Registered '{descriptor.DisplayName}' in {descriptor.Rooms.Count} room(s).");
        return descriptor;
    }

    /// <summary>
    /// 先结束所有关联 Runtime，再注销原先返回的 Descriptor，释放它占用的房间和 Oracle ID。
    /// 重复注销或旧 Descriptor 面对后来注册的新定义时返回 false，绝不移除新定义。
    /// </summary>
    public static bool Unregister(IteratorDescriptor descriptor)
    {
        if (descriptor == null)
            throw new ArgumentNullException(nameof(descriptor));

        Snapshot current = _snapshot;
        if (!current.ByID.TryGetValue(descriptor.ID.Value, out IteratorDescriptor existing) ||
            !ReferenceEquals(existing, descriptor) || !RetiringIDs.Add(descriptor.ID.Value))
            return false;

        var logger = new IteratorLogger(descriptor.ID).ForModule("Registry").ForPhase("Unregister");
        try
        {
            IteratorRuntimes.DestroyForDescriptor(descriptor);
            // OnDestroy may legitimately register/unregister OTHER definitions. Re-read
            // the latest snapshot so those callback changes cannot be lost or resurrected.
            var entries = new List<IteratorDescriptor>(_snapshot.Descriptors);
            entries.Remove(descriptor);
            var next = new Snapshot(entries);
            IteratorOracleIds.Unregister(descriptor.ID);
            _snapshot = next;
        }
        catch (Exception exception)
        {
            logger.Error("Unregistration failed; the registry still retains the descriptor.", exception);
            throw;
        }
        finally
        {
            RetiringIDs.Remove(descriptor.ID.Value);
        }

        logger.Info("Unregistered descriptor and released its room and Oracle ID bindings.");
        return true;
    }

    /// <summary>按 ID 值查询；null 或未注册 ID 返回 false。</summary>
    public static bool TryGet(IteratorID id, out IteratorDescriptor descriptor) => TryGet(id?.Value, out descriptor);

    /// <summary>按区分大小写的字符串查询；null、非法或未注册的字符串返回 false。</summary>
    public static bool TryGet(string id, out IteratorDescriptor descriptor)
    {
        descriptor = null;
        return id != null && _snapshot.ByID.TryGetValue(id, out descriptor);
    }

    /// <summary>按完整房间名查询，忽略大小写；无后缀推断、模糊匹配或自动注册。</summary>
    public static bool TryGetByRoom(string roomName, out IteratorDescriptor descriptor)
    {
        descriptor = null;
        return roomName != null && _snapshot.ByRoom.TryGetValue(roomName, out descriptor);
    }

    /// <summary>将游戏的 Oracle ID 映射到定义；原版或其他 Mod 的 ID 返回 false。</summary>
    public static bool TryGetByOracleID(Oracle.OracleID oracleID, out IteratorDescriptor descriptor) =>
        TryGet(oracleID?.value, out descriptor);

    /// <summary>取得已注册 ID 的游戏值对象。返回独立对象，不向调用方暴露 Registry 持有的可变状态。</summary>
    public static bool TryGetOracleID(IteratorID id, out Oracle.OracleID oracleID)
    {
        oracleID = null;
        if (!TryGet(id, out IteratorDescriptor descriptor))
            return false;
        oracleID = new Oracle.OracleID(descriptor.ID.Value, false);
        return true;
    }

    private sealed class Snapshot
    {
        internal readonly ReadOnlyCollection<IteratorDescriptor> Descriptors;
        internal readonly Dictionary<string, IteratorDescriptor> ByID = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, IteratorDescriptor> ByRoom = new(StringComparer.OrdinalIgnoreCase);

        internal Snapshot(IEnumerable<IteratorDescriptor> descriptors)
        {
            var copy = new List<IteratorDescriptor>(descriptors);
            foreach (IteratorDescriptor descriptor in copy)
            {
                ByID.Add(descriptor.ID.Value, descriptor);
                foreach (string room in descriptor.Rooms)
                    ByRoom.Add(room, descriptor);
            }
            Descriptors = copy.AsReadOnly();
        }
    }
}
