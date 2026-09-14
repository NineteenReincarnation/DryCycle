using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>命名 Sprite 的数据句柄；实际 Futile Sprite 属于各个相机，不能跨相机共享。</summary>
public sealed class SpriteHandle
{
    internal SpriteHandle(string name, IteratorMesh mesh, int layer, string element, string shader)
    { Name = name; Mesh = mesh; Layer = layer; Element = element; Shader = shader; }
    public string Name { get; }
    public IteratorMesh Mesh { get; }
    public int Layer { get; }
    public string Element { get; }
    public string Shader { get; }
    public bool Visible { get; set; } = true;
    internal IteratorGraphicsPart Owner;
}

/// <summary>初始化时注册，之后冻结布局。模块使用名称或句柄，不使用相机 Sprite 数字下标。</summary>
public sealed class SpriteRegistry
{
    private readonly Dictionary<string, SpriteHandle> _names = new(StringComparer.Ordinal);
    private readonly List<SpriteHandle> _entries = new();
    private bool _frozen;
    internal IteratorGraphicsPart RegisteringPart;
    internal SpriteRegistry() => Entries = _entries.AsReadOnly();
    public IReadOnlyList<SpriteHandle> Entries { get; }
    public SpriteHandle this[string name] => _names[name];
    public bool TryGet(string name, out SpriteHandle sprite) => _names.TryGetValue(name, out sprite);
    public SpriteHandle Register(string name, IteratorMesh mesh, int layer = 0, string element = "Futile_White", string shader = "Basic")
    {
        if (_frozen) throw new InvalidOperationException("Sprite layout is frozen after initialization.");
        IteratorValidation.RequireText(name, nameof(name));
        IteratorValidation.RequireText(element, nameof(element));
        IteratorValidation.RequireText(shader, nameof(shader));
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        if (_names.ContainsKey(name)) throw new ArgumentException("Duplicate sprite name: " + name);
        if (mesh.Registry != null) throw new ArgumentException("Each sprite must own its mesh; do not share meshes across registries or instances.");
        var handle = new SpriteHandle(name, mesh, layer, element, shader) { Owner = RegisteringPart };
        _names.Add(name, handle); _entries.Add(handle); mesh.Registry = this;
        return handle;
    }
    internal void Freeze() => _frozen = true;
    internal void Hide(IteratorGraphicsPart part)
    {
        foreach (SpriteHandle entry in _entries) if (part == null || ReferenceEquals(entry.Owner, part)) entry.Visible = false;
    }
}
