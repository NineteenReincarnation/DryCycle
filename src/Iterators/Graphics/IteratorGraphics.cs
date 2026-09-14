using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>插值后的只读绘制输入。Local 使用身体方向和 Profile.Scale；世界手脚目标来自 Body。</summary>
public readonly struct IteratorDrawState
{
    internal IteratorDrawState(GraphicsProfile profile, Vector2 position, Vector2 up, Vector2 leftHand,
        Vector2 rightHand, Vector2 leftFoot, Vector2 rightFoot, Vector2 look, Vector2 velocity, float time)
    {
        Profile = profile; Position = position; Up = up; Right = new Vector2(up.y, -up.x);
        LeftHand = leftHand; RightHand = rightHand; LeftFoot = leftFoot; RightFoot = rightFoot;
        Look = look; Velocity = velocity; Time = time;
    }
    public GraphicsProfile Profile { get; }
    public Vector2 Position { get; }
    public Vector2 Up { get; }
    public Vector2 Right { get; }
    public Vector2 LeftHand { get; }
    public Vector2 RightHand { get; }
    public Vector2 LeftFoot { get; }
    public Vector2 RightFoot { get; }
    public Vector2 Look { get; }
    public Vector2 Velocity { get; }
    public float Time { get; }
    public Vector2 Local(float x, float y) => Position + (Right * x + Up * y) * Profile.Scale;
    public Vector2 Local(Vector2 point) => Local(point.x, point.y);
}

/// <summary>独立可替换的视觉部件。异常停用本部件，已注册的 Sprite 自动隐藏并仍参与清理。</summary>
public abstract class IteratorGraphicsPart
{
    protected IteratorGraphicsPart(string name) { IteratorValidation.RequireText(name, nameof(name)); Name = name; }
    public string Name { get; }
    public IteratorGraphics Graphics { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    public bool IsDestroyed { get; private set; }
    protected IteratorContext Context => Graphics.Context;
    protected GraphicsProfile Profile => Graphics.Profile;
    protected SpriteRegistry Sprites => Graphics.Sprites;
    protected abstract void OnInitialize();
    protected virtual void OnUpdate() { }
    protected abstract void OnDraw(IteratorDrawState state);
    protected virtual void OnDestroy() { }
    protected SpriteHandle Register(string name, IteratorMesh mesh, int layer = 0) =>
        Sprites.Register(Name + "." + name, mesh, layer, Profile.Element, Profile.Shader);
    internal void Initialize(IteratorGraphics graphics)
    {
        if (Graphics != null || IsDestroyed) throw new InvalidOperationException("Graphics parts cannot be reused.");
        Graphics = graphics;
        Invoke("Initialize", OnInitialize);
    }
    internal void Update()
    {
        if (!IsEnabled || IsDestroyed) return;
        try { OnUpdate(); }
        catch (Exception exception) { Fail("Update", exception); }
    }
    internal void Draw(IteratorDrawState state)
    {
        if (!IsEnabled || IsDestroyed) return;
        try { OnDraw(state); }
        catch (Exception exception) { Fail("Draw", exception); }
    }
    private void Invoke(string phase, Action callback)
    {
        if (!IsEnabled || IsDestroyed) return;
        try { callback(); }
        catch (Exception exception) { Fail(phase, exception); }
    }
    private void Fail(string phase, Exception exception)
    {
        IsEnabled = false;
        Graphics.Sprites.Hide(this);
        Graphics.Log.ForModule("Graphics." + Name).ForPhase(phase).Error("Visual part disabled; runtime remains active.", exception);
    }
    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true; IsEnabled = false;
        try { OnDestroy(); }
        catch (Exception exception) { Graphics.Log.ForModule("Graphics." + Name).ForPhase("Destroy").Error("Visual cleanup failed.", exception); }
        finally { Graphics.Sprites.Hide(this); }
    }
}

/// <summary>每 Runtime 一个图形组件，CPU 数据与各相机的 Sprite 分离。无需修改全局 OracleGraphics Hook。</summary>
public abstract class IteratorGraphics
{
    private readonly List<IteratorGraphicsPart> _parts = new();
    private readonly List<string> _atlases = new();
    private readonly List<IDisposable> _leases = new();
    private bool _initializing, _rendering, _assetsLoaded;
    private Snapshot _previous, _current;
    private long _ticks;

    protected IteratorGraphics(IteratorContext context, GraphicsProfile profile = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Profile = profile ?? GraphicsProfile.Default;
        Sprites = new SpriteRegistry(); Parts = _parts.AsReadOnly();
        Log = new IteratorLogger(context.ID).ForModule("Graphics");
    }
    public IteratorContext Context { get; }
    public GraphicsProfile Profile { get; }
    public SpriteRegistry Sprites { get; }
    public IReadOnlyList<IteratorGraphicsPart> Parts { get; }
    public bool IsInitialized { get; private set; }
    public bool IsDestroyed { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    internal bool Claimed;
    internal IteratorLogger Log { get; }
    protected abstract void OnInitialize();
    protected virtual void OnUpdate() { }
    protected virtual void OnDraw(IteratorDrawState state) { }
    protected virtual void OnDestroy() { }

    /// <summary>仅在 OnInitialize 组合部件；按添加顺序更新，以 Layer 决定绘制顺序。</summary>
    protected void AddPart(IteratorGraphicsPart part)
    {
        if (!_initializing || IsDestroyed) throw new InvalidOperationException("Add parts only during graphics initialization.");
        if (part == null) throw new ArgumentNullException(nameof(part));
        if (part.Graphics != null || part.IsDestroyed) throw new InvalidOperationException("Graphics parts cannot be reused.");
        foreach (IteratorGraphicsPart existing in _parts) if (existing.Name == part.Name) throw new ArgumentException("Duplicate graphics part: " + part.Name);
        _parts.Add(part);
        Sprites.RegisteringPart = part;
        try { part.Initialize(this); }
        finally { Sprites.RegisteringPart = null; }
    }

    /// <summary>声明 Futile Atlas 路径（无扩展名）。首次创建相机 Sprite 时按引用计数加载；不卸载借用的游戏资源。</summary>
    protected void RequireAtlas(string path)
    {
        if (!_initializing || IsDestroyed) throw new InvalidOperationException("Declare resources during initialization.");
        IteratorValidation.RequireText(path, nameof(path));
        if (!_atlases.Contains(path)) _atlases.Add(path);
    }

    internal void Initialize()
    {
        _initializing = true;
        try
        {
            OnInitialize();
            if (IsDestroyed) return;
            Sprites.Freeze();
            _previous = _current = Capture();
            IsInitialized = true;
            Render(1f);
        }
        finally { _initializing = false; }
    }

    internal void Update()
    {
        if (!IsInitialized || IsDestroyed || !IsEnabled) return;
        try
        {
            _previous = _current; _current = Capture(); _ticks++;
            OnUpdate();
            foreach (IteratorGraphicsPart part in _parts) { if (IsDestroyed) break; part.Update(); }
            (Context.Oracle?.graphicsModule as IteratorGraphicsHost)?.PruneViews();
        }
        catch (Exception exception) { Disable("Update", exception); }
    }

    /// <summary>写入同一套世界坐标网格供游戏和预览消费；不推进动画时钟，不修改 Body。</summary>
    public void Render(float interpolation = 1f)
    {
        if (!IsInitialized || IsDestroyed || !IsEnabled || _rendering) return;
        _rendering = true;
        try
        {
            float t = GraphicsProfile.Finite(interpolation) ? Mathf.Clamp01(interpolation) : 1f;
            Vector2 up = Vector2.Lerp(_previous.Up, _current.Up, t);
            up = up.sqrMagnitude < 0.00001f ? _current.Up : up.normalized;
            var state = new IteratorDrawState(Profile, Vector2.Lerp(_previous.Position, _current.Position, t), up,
                Vector2.Lerp(_previous.LeftHand, _current.LeftHand, t), Vector2.Lerp(_previous.RightHand, _current.RightHand, t),
                Vector2.Lerp(_previous.LeftFoot, _current.LeftFoot, t), Vector2.Lerp(_previous.RightFoot, _current.RightFoot, t),
                Vector2.Lerp(_previous.Look, _current.Look, t), _current.Velocity, (_ticks + t) / 40f);
            OnDraw(state);
            foreach (IteratorGraphicsPart part in _parts) { if (IsDestroyed) break; part.Draw(state); }
        }
        catch (Exception exception) { Disable("Draw", exception); }
        finally { _rendering = false; }
    }

    internal void LoadAssets()
    {
        if (_assetsLoaded || IsDestroyed) return;
        _assetsLoaded = true;
        foreach (string atlas in _atlases)
        {
            try { _leases.Add(IteratorAtlasCache.Acquire(atlas)); }
            catch (Exception exception) { Log.ForPhase("Assets").Error("Atlas unavailable; missing elements will use Futile_White: " + atlas, exception); }
        }
    }

    internal void Disable(string phase, Exception exception)
    {
        if (!IsEnabled || IsDestroyed) return;
        IsEnabled = false; Sprites.Hide(null);
        Log.ForPhase(phase).Error("Graphics disabled; runtime remains active.", exception);
    }

    internal void Release()
    {
        if (IsDestroyed) return;
        IsDestroyed = true; IsEnabled = false; Sprites.Freeze();
        // Remove live sprites before releasing atlas textures.
        (Context.Oracle?.graphicsModule as IteratorGraphicsHost)?.ReleaseViews();
        for (int i = _parts.Count - 1; i >= 0; i--) _parts[i].Release();
        try { OnDestroy(); }
        catch (Exception exception) { Log.ForPhase("Destroy").Error("Graphics cleanup failed.", exception); }
        finally
        {
            Sprites.Hide(null);
            foreach (IDisposable lease in _leases)
            {
                try { lease.Dispose(); }
                catch (Exception exception) { Log.ForPhase("Assets").Error("Atlas cleanup failed.", exception); }
            }
            _leases.Clear();
        }
    }

    private Snapshot Capture()
    {
        IteratorBody body = Context.Body;
        return new Snapshot { Position = body.Position, Up = body.Direction, Velocity = body.Velocity,
            LeftHand = body.LeftHandPosition, RightHand = body.RightHandPosition,
            LeftFoot = body.LeftFootPosition, RightFoot = body.RightFootPosition,
            Look = body.LookPoint.HasValue ? Vector2.ClampMagnitude(body.LookPoint.Value - body.Position, 100f) / 100f : Vector2.zero };
    }
    private struct Snapshot
    {
        internal Vector2 Position, Up, Velocity, LeftHand, RightHand, LeftFoot, RightFoot, Look;
    }
}
