using System;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace DryCycle.Iterators;

/// <summary>所有游戏 Hook 的唯一安装点；失败时回滚，停用时先销毁实例再解除补丁。</summary>
internal static class IteratorHooks
{
    private static bool _installed;
    private static bool _installing;
    private static bool _uninstalling;
    private static bool _constructorHook;
    private static bool _roomReadyHook;
    private static bool _roomUnloadHook;
    private static bool _sessionHook;
    private static bool _removeHook;
    private static readonly IteratorLogger Logger = new IteratorLogger(new IteratorID("IteratorFramework")).ForModule("Hooks");

    internal static bool Install()
    {
        if (_installed) return true;
        if (_installing || _uninstalling) return false;
        // An earlier backend failure may have left a disabled callback installed.
        // Remove it before adding anything, rather than registering it twice.
        if (_constructorHook || _roomReadyHook || _roomUnloadHook || _sessionHook || _removeHook)
        {
            Uninstall();
            if (_constructorHook || _roomReadyHook || _roomUnloadHook || _sessionHook || _removeHook) return false;
        }
        _installing = true;
        try
        {
            _constructorHook = true;
            IL.Oracle.ctor += PatchOracleConstructor;
            IteratorPhysicsAdapter.Prepare();
            _roomReadyHook = true;
            On.Room.ReadyForAI += RoomReady;
            _roomUnloadHook = true;
            On.Room.Unloaded += RoomUnloaded;
            _sessionHook = true;
            On.RainWorldGame.ShutDownProcess += SessionEnded;
            _removeHook = true;
            On.UpdatableAndDeletable.RemoveFromRoom += RemovedFromRoom;
            _installed = true;
            IteratorRuntimes.Enable();
            return true;
        }
        catch (Exception exception)
        {
            Uninstall();
            Logger.ForPhase("Install").Error("Iterator runtime hooks could not be installed; registration remains available.", exception);
            return false;
        }
        finally
        {
            _installing = false;
        }
    }

    internal static void Uninstall()
    {
        if (_uninstalling) return;
        _uninstalling = true;
        try
        {
            _installed = false;
            IteratorRuntimes.Disable();
            Remove(ref _removeHook, () => On.UpdatableAndDeletable.RemoveFromRoom -= RemovedFromRoom);
            Remove(ref _sessionHook, () => On.RainWorldGame.ShutDownProcess -= SessionEnded);
            Remove(ref _roomUnloadHook, () => On.Room.Unloaded -= RoomUnloaded);
            Remove(ref _roomReadyHook, () => On.Room.ReadyForAI -= RoomReady);
            Remove(ref _constructorHook, () => IL.Oracle.ctor -= PatchOracleConstructor);
        }
        finally
        {
            _uninstalling = false;
        }
    }

    private static void Remove(ref bool installed, Action remove)
    {
        if (!installed) return;
        try
        {
            remove();
            installed = false;
        }
        catch (Exception exception)
        {
            Logger.ForPhase("Uninstall").Error("A hook could not be removed; its callback remains disabled.", exception);
        }
    }

    internal static void PatchOracleConstructor(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, instruction => instruction.MatchCall<PhysicalObject>(".ctor")))
            throw new InvalidOperationException("IteratorFramework: Oracle constructor no longer has the expected PhysicalObject base constructor; refusing to patch an unknown layout.");

        ILLabel vanilla = cursor.DefineLabel();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.Emit(OpCodes.Ldarg_2);
        cursor.EmitDelegate<Func<Oracle, AbstractPhysicalObject, Room, bool>>(IteratorHost.InitializeOracle);
        cursor.Emit(OpCodes.Brfalse, vanilla);
        cursor.Emit(OpCodes.Ret);
        cursor.MarkLabel(vanilla);
    }

    private static void RoomReady(On.Room.orig_ReadyForAI orig, Room self)
    {
        orig(self);
        if (_installed) IteratorRuntimes.RoomReady(self);
    }

    internal static void RoomUnloaded(On.Room.orig_Unloaded orig, Room self)
    {
        if (_installed) IteratorRuntimes.RoomUnloaded(self);
        orig(self);
    }

    internal static void SessionEnded(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
    {
        if (_installed) IteratorRuntimes.SessionEnded(self);
        orig(self);
    }

    private static void RemovedFromRoom(On.UpdatableAndDeletable.orig_RemoveFromRoom orig, UpdatableAndDeletable self)
    {
        if (_installed && self is IteratorHost host) host.RemovedFromRoom();
        orig(self);
    }
}
