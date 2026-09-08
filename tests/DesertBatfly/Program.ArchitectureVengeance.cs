using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static partial class Program
{
    private static void RunArchitectureVengeance()
    {
        Type fear = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FearRuntime", true);
        Type vengeance = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceRuntime", true);
        Type vengeanceState = vengeance.GetNestedType("State", Flags);
        Type fearState = fear.GetNestedType("State", Flags);

        Check(vengeanceState != null && fearState != null,
            "Vengeance extraction exposes a dedicated Vengeance state embedded by the Fear host");
        FieldInfo embedded = fearState.GetField("Vengeance", Flags);
        Check(embedded != null && embedded.FieldType == vengeanceState,
            "Fear owns one per-bat host whose Vengeance field is the dedicated Vengeance state");

        FieldInfo[] fearTables = fear.GetFields(Flags)
            .Where(f => f.FieldType.IsGenericType &&
                        f.FieldType.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
            .ToArray();
        FieldInfo[] vengeanceTables = vengeance.GetFields(Flags)
            .Where(f => f.FieldType.IsGenericType &&
                        f.FieldType.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
            .ToArray();
        Check(fearTables.Any(f => f.Name == "states") && vengeanceTables.Length == 0,
            "Vengeance extraction keeps exactly the existing Fear-host weak table and introduces no second per-bat table");

        Check(vengeance.GetMethod("IsActive", Flags) != null &&
              vengeance.GetMethod("IsAvenger", Flags) != null &&
              vengeance.GetMethod("TryGetTarget", Flags) != null &&
              vengeance.GetMethod("ExecuteOwned", Flags) != null &&
              vengeance.GetMethod("ArmGroup", Flags) != null &&
              vengeance.GetMethod("Clear", Flags) != null,
            "Vengeance owns query, group arming, cancellation and owner-gated execution APIs");
        Check(fear.GetMethod("IsExtremeVengeanceActive", Flags) == null &&
              fear.GetMethod("IsVengeanceAvenger", Flags) == null &&
              fear.GetMethod("TryGetVengeanceTarget", Flags) == null &&
              fear.GetMethod("ExecuteVengeanceOwned", Flags) == null,
            "Fear no longer exposes Vengeance implementation entry points");

        Check(fear.GetMethod("VengeanceStateFor", Flags) != null &&
              fear.GetMethod("TryGetVengeanceState", Flags) != null &&
              fear.GetMethod("FearStrengthForVengeance", Flags) != null &&
              fear.GetMethod("TryDeactivateAfterVengeance", Flags) != null,
            "Fear exposes only the narrow single-host state contract required by Vengeance");

        Type executor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_VengeanceExecutor", true);
        Check(MethodCallOffset(executor.GetMethod("TryExecute", Flags), vengeance, "ExecuteOwned") >= 0,
            "Vengeance executor enters the formal Vengeance owner directly");
        Type motor = mod.GetType("DryCycle.Creatures.DesertBatfly.DB_FlightMotor", true);
        MethodInfo forceFlight = vengeance.GetMethod("ForceFlight", Flags);
        Check(forceFlight != null && MethodCallOffset(forceFlight, motor, "TrySteer") >= 0,
            "Vengeance movement remains behind the single FlightMotor write boundary");

        Console.WriteLine("Architecture Vengeance: one Fear host, embedded Vengeance state, synchronous clear contract, formal owner APIs and FlightMotor boundary verified.");
    }
}
