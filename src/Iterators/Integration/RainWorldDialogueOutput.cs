using System;
using System.Collections.Generic;
using System.Reflection;

namespace DryCycle.Iterators;

/// <summary>通过房间相机的原版 HUD.DialogBox 显示文字；不需要 Conversation.ID，也不清空共享队列。</summary>
public sealed class RainWorldDialogueOutput : IDialogueOutput
{
    // InitNextMessage is private in the shipped game. Keep this one reflection
    // boundary here instead of calling publicized-only members from runtime code.
    private static readonly MethodInfo InitNext = typeof(global::HUD.DialogBox).GetMethod("InitNextMessage",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

    public IDialogueLine TryShow(DialogueContext context, string text, int extraLinger)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        IteratorContext iterator = context.Iterator;
        RoomCamera[] cameras = iterator?.Game?.cameras;
        if (cameras == null || iterator.Room == null) return null;
        var views = new List<RoomCamera>();
        var boxes = new List<global::HUD.DialogBox>();
        foreach (RoomCamera camera in cameras)
        {
            if (camera?.hud == null || !ReferenceEquals(camera.room, iterator.Room)) continue;
            global::HUD.DialogBox box = camera.hud.dialogBox ?? camera.hud.InitDialogBox();
            if (box == null || boxes.Contains(box)) continue;
            // Let existing vanilla/mod dialogue finish before claiming a line.
            if (box.messages.Count != 0 || box.permanentDisplay) return null;
            views.Add(camera); boxes.Add(box);
        }
        if (boxes.Count == 0) return null;
        if (InitNext == null) throw new MissingMethodException("HUD.DialogBox.InitNextMessage is unavailable in this game version.");
        string translated = iterator.Game.rainWorld?.inGameTranslator?.Translate(text) ?? text;
        var line = new Line(iterator.Room);
        try
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                global::HUD.DialogBox box = boxes[i];
                var message = new global::HUD.DialogBox.Message(translated, box.defaultXOrientation, box.defaultYPos, extraLinger);
                line.Entries.Add(new Entry(views[i], box, message));
                box.NewMessage(message);
            }
            return line;
        }
        catch { line.Dispose(); throw; }
    }

    internal static void RemoveOwned(global::HUD.DialogBox box, global::HUD.DialogBox.Message message)
    {
        int index = box.messages.IndexOf(message);
        if (index < 0) return;
        box.messages.RemoveAt(index);
        if (index != 0 || box.label == null) return;
        if (box.messages.Count != 0) InitNext?.Invoke(box, null);
        else box.label.text = string.Empty; // Draw hides the box when CurrentMessage is null.
    }
    private sealed class Entry
    {
        internal readonly RoomCamera Camera;
        internal readonly global::HUD.DialogBox Box;
        internal readonly global::HUD.DialogBox.Message Message;
        internal Entry(RoomCamera camera, global::HUD.DialogBox box, global::HUD.DialogBox.Message message)
        { Camera = camera; Box = box; Message = message; }
    }
    private sealed class Line : IDialogueLine
    {
        internal readonly List<Entry> Entries = new();
        private Room _room;
        internal Line(Room room) => _room = room;
        public bool IsComplete
        {
            get
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    Entry entry = Entries[i];
                    if (ReferenceEquals(entry.Camera.room, _room) && ReferenceEquals(entry.Camera.hud?.dialogBox, entry.Box)
                        && entry.Box.messages.Contains(entry.Message)) continue;
                    RemoveOwned(entry.Box, entry.Message); Entries.RemoveAt(i);
                }
                if (Entries.Count == 0) _room = null;
                return Entries.Count == 0;
            }
        }
        public void Dispose()
        {
            Exception failure = null;
            foreach (Entry entry in Entries)
            { try { RemoveOwned(entry.Box, entry.Message); } catch (Exception exception) { failure ??= exception; } }
            Entries.Clear(); _room = null;
            if (failure != null) throw failure;
        }
    }
}
