using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>第一阶段唯一的游戏 ID 适配层。没有 Oracle 实体、Room、Hook 或 Session 引用。</summary>
internal static class IteratorOracleIds
{
    // Reserve MSC identities even when MSC is disabled and has not registered its ExtEnums.
    // Verified against the installed Oracle.OracleID and MoreSlugcatsEnums.OracleID.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "SS", "SL", "SS_Cutscene", "SL_Cutscene", "ST_Cutscene", "DM", "ST", "CL"
    };

    internal static void ValidateAvailable(IteratorID id)
    {
        if (Reserved.Contains(id.Value))
            throw new InvalidOperationException($"IteratorFramework: iterator '{id}' conflicts with a reserved vanilla Oracle ID.");

        if (new Oracle.OracleID(id.Value, false).Index >= 0)
            throw new InvalidOperationException(
                $"IteratorFramework: iterator '{id}' conflicts with an Oracle ID already registered outside this framework.");
    }

    internal static void Register(IteratorID id) => _ = new Oracle.OracleID(id.Value, true);

    internal static void Unregister(IteratorID id)
    {
        var oracleID = new Oracle.OracleID(id.Value, false);
        if (oracleID.Index >= 0)
            oracleID.Unregister();
    }
}
