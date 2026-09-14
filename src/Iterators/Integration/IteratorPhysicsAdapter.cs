using System;
using System.Reflection;
using System.Reflection.Emit;

namespace DryCycle.Iterators;

/// <summary>一次生成非虚 base 调用：C# 不能直接调用 Oracle 的祖先 PhysicalObject.Update。</summary>
internal static class IteratorPhysicsAdapter
{
    private static Action<PhysicalObject, bool> _update;

    internal static void Prepare()
    {
        if (_update != null) return;
        MethodInfo method = typeof(PhysicalObject).GetMethod(nameof(PhysicalObject.Update),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new[] { typeof(bool) }, null);
        if (method == null || method.ReturnType != typeof(void))
            throw new MissingMethodException("IteratorFramework: PhysicalObject.Update(bool) is unavailable.");
        var bridge = new DynamicMethod("Iterator_PhysicalObject_Update", typeof(void),
            new[] { typeof(PhysicalObject), typeof(bool) }, typeof(IteratorPhysicsAdapter).Module, true);
        ILGenerator il = bridge.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        _update = (Action<PhysicalObject, bool>)bridge.CreateDelegate(typeof(Action<PhysicalObject, bool>));
    }

    internal static void Update(PhysicalObject host, bool evenUpdate) => _update(host, evenUpdate);
}
