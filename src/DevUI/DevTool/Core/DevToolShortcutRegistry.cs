using System;
using System.Collections.Generic;
using System.Linq;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Presentation-agnostic shortcut metadata shared by DevTool frontends.
/// The registry deliberately owns only labels and scope; input handling remains in EditorInputRouter
/// and the individual editor tools. Mods may register additional rows without referencing RWImGui.
/// </summary>
public sealed class DevToolShortcutDescriptor
{
    public DevToolShortcutDescriptor(
        string id,
        string input,
        string chineseDescription,
        string englishDescription,
        int order = 0)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Shortcut id is required.", nameof(id)) : id.Trim();
        Input = input?.Trim() ?? string.Empty;
        ChineseDescription = chineseDescription?.Trim() ?? string.Empty;
        EnglishDescription = englishDescription?.Trim() ?? string.Empty;
        Order = order;
    }

    public string Id { get; }
    public string Input { get; }
    public string ChineseDescription { get; }
    public string EnglishDescription { get; }
    public int Order { get; }
}

/// <summary>
/// One central shortcut catalog for the rebuilt DevTool.
/// Duplicate ids replace the previous row inside the same scope, which lets compatibility layers
/// refine wording without creating duplicate UI entries. Getters return snapshots safe to enumerate
/// from the RWImGui render thread.
/// </summary>
public static class DevToolShortcutRegistry
{
    private sealed class Registration
    {
        internal DevToolShortcutDescriptor Descriptor;
        internal long Sequence;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Registration> Common = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<EditorToolMode, Dictionary<string, Registration>> Modes = new();
    private static long sequence;

    static DevToolShortcutRegistry()
    {
        RegisterBuiltIns();
    }

    public static void RegisterCommon(DevToolShortcutDescriptor descriptor) =>
        Register(Common, descriptor);

    public static void RegisterMode(EditorToolMode mode, DevToolShortcutDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (Gate)
        {
            if (!Modes.TryGetValue(mode, out Dictionary<string, Registration> bucket))
            {
                bucket = new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);
                Modes[mode] = bucket;
            }
            RegisterLocked(bucket, descriptor);
        }
    }

    public static bool UnregisterCommon(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate) return Common.Remove(id.Trim());
    }

    public static bool UnregisterMode(EditorToolMode mode, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
            return Modes.TryGetValue(mode, out Dictionary<string, Registration> bucket) && bucket.Remove(id.Trim());
    }

    public static DevToolShortcutDescriptor[] GetCommon()
    {
        lock (Gate) return Snapshot(Common);
    }

    public static DevToolShortcutDescriptor[] GetMode(EditorToolMode mode)
    {
        lock (Gate)
            return Modes.TryGetValue(mode, out Dictionary<string, Registration> bucket)
                ? Snapshot(bucket)
                : Array.Empty<DevToolShortcutDescriptor>();
    }

    private static void Register(Dictionary<string, Registration> bucket, DevToolShortcutDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (Gate) RegisterLocked(bucket, descriptor);
    }

    private static void RegisterLocked(Dictionary<string, Registration> bucket, DevToolShortcutDescriptor descriptor)
    {
        if (bucket.TryGetValue(descriptor.Id, out Registration current))
        {
            current.Descriptor = descriptor;
            return;
        }

        bucket[descriptor.Id] = new Registration
        {
            Descriptor = descriptor,
            Sequence = sequence++
        };
    }

    private static DevToolShortcutDescriptor[] Snapshot(Dictionary<string, Registration> bucket) =>
        bucket.Values
            .OrderBy(value => value.Descriptor.Order)
            .ThenBy(value => value.Sequence)
            .Select(value => value.Descriptor)
            .ToArray();

    private static void RegisterBuiltIns()
    {
        RegisterCommon(new DevToolShortcutDescriptor("save", "Ctrl+S", "保存当前编辑内容", "Save current edits", 10));
        RegisterCommon(new DevToolShortcutDescriptor("undo", "Ctrl+Z", "撤销上一步", "Undo last action", 20));
        RegisterCommon(new DevToolShortcutDescriptor("redo-shift", "Ctrl+Shift+Z", "重做", "Redo", 30));
        RegisterCommon(new DevToolShortcutDescriptor("redo-y", "Ctrl+Y", "重做（备用快捷键）", "Redo (alternate)", 31));
        RegisterCommon(new DevToolShortcutDescriptor("focus", "Tab", "进入 / 退出专注模式", "Enter / exit Focus mode", 40));
        RegisterCommon(new DevToolShortcutDescriptor("browser", "Ctrl+B", "显示 / 隐藏浏览器", "Show / hide Browser", 50));
        RegisterCommon(new DevToolShortcutDescriptor("inspector", "Ctrl+I", "显示 / 隐藏检查器", "Show / hide Inspector", 60));
        RegisterCommon(new DevToolShortcutDescriptor("presentation", "Ctrl+Shift+U", "切换新 UI / 原版 DevUI", "Toggle New UI / Vanilla DevUI", 70));
        RegisterCommon(new DevToolShortcutDescriptor("escape", "Esc", "取消当前放置；否则隐藏 DevTool UI", "Cancel placement; otherwise hide DevTool UI", 80));
        RegisterCommon(new DevToolShortcutDescriptor("window-marquee", "Shift+左键拖框 / Shift+drag", "多选编辑器窗口", "Multi-select editor windows", 100));
        RegisterCommon(new DevToolShortcutDescriptor("window-group", "Ctrl+G", "将当前选中的窗口编组", "Group selected windows", 110));
        RegisterCommon(new DevToolShortcutDescriptor("window-group-move", "拖动组内标题栏 / Drag title", "整组移动已编组窗口", "Move the whole window group", 120));

        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-duplicate", "Ctrl+D", "复制当前选中物件", "Duplicate selected objects", 10));
        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-delete", "Delete", "删除当前选中物件", "Delete selected objects", 20));
        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-toggle-selection", "Ctrl+左键 / Ctrl+click", "切换场景列表中的单个物件选择", "Toggle one object in the Scene selection", 30));
        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-range-selection", "Shift+左键 / Shift+click", "从上次锚点范围选择场景物件", "Range-select Scene objects from the last anchor", 40));
        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-place", "左键 / Click", "放置当前选择的物件", "Place the selected object", 50));
        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-place-repeat", "Shift+左键 / Shift+click", "连续放置当前物件", "Place repeatedly", 60));
        RegisterMode(EditorToolMode.Objects,
            new DevToolShortcutDescriptor("objects-place-cancel", "右键 / Esc", "取消物件放置", "Cancel object placement", 70));

        RegisterMode(EditorToolMode.Sound,
            new DevToolShortcutDescriptor("sound-handle", "左键拖动 / Drag", "拖动世界中的声音控制点；控制点优先于覆盖在其上的窗口", "Drag sound world handles; handles take priority over overlapping windows", 10));
    }
}
