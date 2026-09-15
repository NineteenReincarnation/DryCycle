using System;
using UnityEngine;

namespace DryCycle.Iterators;

/// <summary>一次播放的上下文。结束通知之后清空 Controller、Iterator 和 Player；不充当存档。</summary>
public sealed class DialogueContext
{
    private int _lookMode;
    private Vector2 _look, _move;
    private bool _hasMove, _appliedLook, _appliedMove, _appliedPose;
    private Vector2? _oldLook, _lastLook, _oldMove, _lastMove;
    private IteratorPose _pose, _oldPose, _lastPose;
    internal DialogueContext(ConversationController controller, Player player)
    { Controller = controller; Iterator = controller.Context; Player = player; HasTarget = player != null; }
    public ConversationController Controller { get; private set; }
    public IteratorContext Iterator { get; private set; }
    public Player Player { get; private set; }
    public bool HasTarget { get; }
    public bool IsPlayerAvailable => Player != null && Iterator?.Room != null &&
        ReferenceEquals(Player.room, Iterator.Room) && !Player.dead && !Player.inShortcut && !Player.slatedForDeletetion;
    public void LookAtPlayer() { EnsureAttached(); _lookMode = 2; }
    public void LookAt(Vector2 position) { EnsureAttached(); _look = BodyValidation.Vector(position, nameof(position)); _lookMode = 1; }
    public void MoveTo(Vector2 position) { EnsureAttached(); _move = BodyValidation.Vector(position, nameof(position)); _hasMove = true; }
    public void Gesture(IteratorPose pose) { EnsureAttached(); _pose = pose ?? throw new ArgumentNullException(nameof(pose)); }
    private void EnsureAttached() { if (Iterator == null) throw new ObjectDisposedException(nameof(DialogueContext)); }

    // Apply after Brain, before Runtime.OnUpdate. Restore only inputs still matching
    // our own last write, so a later external controller retains its override.
    internal void ApplyInputs()
    {
        IteratorBody body = Iterator?.Body;
        if (body == null || body.IsDestroyed) return;
        Vector2? look = _lookMode == 1 ? _look : null;
        if (_lookMode == 2 && IsPlayerAvailable && Player.bodyChunks != null && Player.bodyChunks.Length > 0)
            look = Player.bodyChunks[0].pos;
        if (look.HasValue)
        {
            if (!_appliedLook || body.LookPoint != _lastLook) _oldLook = body.LookPoint;
            body.LookAt(look.Value); _lastLook = look; _appliedLook = true;
        }
        if (_hasMove)
        {
            if (!_appliedMove || body.MovementTarget != _lastMove) _oldMove = body.MovementTarget;
            body.MoveTo(_move); _lastMove = _move; _appliedMove = true;
        }
        if (_pose != null)
        {
            if (!_appliedPose || !ReferenceEquals(body.Pose, _lastPose)) _oldPose = body.Pose;
            body.SetPose(_pose); _lastPose = _pose; _appliedPose = true;
        }
    }
    internal void ReleaseInputs()
    {
        IteratorBody body = Iterator?.Body;
        if (body != null && !body.IsDestroyed && Iterator.Runtime.IsActive)
        {
            if (_appliedLook && body.LookPoint == _lastLook)
            { if (_oldLook.HasValue) body.LookAt(_oldLook.Value); else body.ClearLookTarget(); }
            if (_appliedMove && body.MovementTarget == _lastMove)
            { if (_oldMove.HasValue) body.MoveTo(_oldMove.Value); else body.ReleaseMovement(); }
            if (_appliedPose && ReferenceEquals(body.Pose, _lastPose)) body.SetPose(_oldPose);
        }
        _appliedLook = _appliedMove = _appliedPose = false;
        _oldLook = _lastLook = _oldMove = _lastMove = null;
        _oldPose = _lastPose = null;
    }
    internal void Detach()
    {
        try { ReleaseInputs(); }
        finally { _pose = _oldPose = _lastPose = null; Player = null; Iterator = null; Controller = null; }
    }
}

public enum DialogueStatus { Queued, Running, Paused, Interrupted, Completed, Cancelled, Failed }
public enum DialogueEndReason { Completed, Cancelled, Replaced, TargetUnavailable, Failed, ControllerDisabled, Destroyed }
