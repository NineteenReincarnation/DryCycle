namespace DryCycle.Iterators;

/// <summary>默认无机械臂模式，不施加锚点、距离或关节约束。</summary>
public sealed class NoArm : IteratorArm
{
    public NoArm(IteratorContext context) : base(context) { }
}
