using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DryCycle.Iterators;

public enum IteratorPlayerEventKind { Entered, Left, Approached, MovedAway, Died, Revived }

/// <summary>同步感知通知；Left 回调返回后 Observation 会清空 Player 引用，不应作为持久事件记录。</summary>
public readonly struct IteratorPlayerEvent
{
    internal IteratorPlayerEvent(IteratorPlayerEventKind kind, PlayerObservation observation) { Kind = kind; Observation = observation; }
    public IteratorPlayerEventKind Kind { get; }
    public PlayerObservation Observation { get; }
    public Player Player => Observation.Player;
}

/// <summary>只读实时观测记录。玩家离开或 Brain 释放后，保留的记录会失效并清空 Player。</summary>
public sealed class PlayerObservation
{
    internal PlayerObservation(PlayerSensor sensor, Player player) { Sensor = sensor; Player = player; IsPresent = true; }
    internal PlayerSensor Sensor { get; }
    internal long Seen;
    public Player Player { get; internal set; }
    public bool IsPresent { get; internal set; }
    public bool IsAlive { get; internal set; }
    public bool InShortcut { get; internal set; }
    public bool HasPosition { get; internal set; }
    public Vector2 Position { get; internal set; }
    public float Distance { get; internal set; } = float.PositiveInfinity;
    public bool IsVisible { get; internal set; }
    public bool IsNear { get; internal set; }
    internal void Clear()
    { Player = null; IsPresent = IsAlive = HasPosition = IsVisible = IsNear = false; InShortcut = false; Position = Vector2.zero; Distance = float.PositiveInfinity; }
}

/// <summary>集中缓存当前房间玩家、距离、可见性及接近状态。只遍历 Context.Players，不扫描世界对象。</summary>
public sealed class PlayerSensor
{
    private readonly IteratorBrain _brain;
    private readonly Dictionary<Player, PlayerObservation> _byPlayer = new(PlayerIdentity.Instance);
    private readonly List<PlayerObservation> _players = new();
    private readonly List<PlayerObservation> _retired = new();
    private readonly List<IteratorPlayerEvent> _events = new();
    private int _untilSample;
    internal PlayerSensor(IteratorBrain brain, IteratorSensorProfile profile)
    { _brain = brain; Profile = profile; Players = _players.AsReadOnly(); }
    public IteratorSensorProfile Profile { get; }
    public IReadOnlyList<PlayerObservation> Players { get; }
    public PlayerObservation NearestVisiblePlayer { get; private set; }
    public long SampleCount { get; private set; }
    public bool IsEnabled { get; private set; } = true;
    internal IReadOnlyList<IteratorPlayerEvent> Events => _events;
    public bool TryGet(Player player, out PlayerObservation observation)
    {
        observation = null;
        return player != null && _byPlayer.TryGetValue(player, out observation);
    }
    /// <summary>请求在下次 Brain 更新时采样；不会重入当前采样或通知。</summary>
    public void RequestRefresh() => _untilSample = 0;
    public bool CanObserve(PlayerObservation observation, bool retaining = false) => IsEnabled && observation != null &&
        ReferenceEquals(observation.Sensor, this) && observation.IsPresent && observation.IsAlive &&
        !observation.InShortcut && observation.HasPosition && observation.IsVisible &&
        observation.Distance <= (retaining ? Profile.RetainRange : Profile.ObserveRange);

    internal bool Update()
    {
        if (!IsEnabled || _brain.IsDestroyed) return false;
        if (_untilSample-- > 0) return false;
        _untilSample = Profile.SampleInterval - 1;
        try { Sample(); return true; }
        catch (Exception exception)
        {
            _brain.Log.ForModule("Sensors.Players").ForPhase("Sample").Error("Player sensing disabled; behavior can fall back to Idle.", exception);
            Release(); return false;
        }
    }

    private void Sample()
    {
        SampleCount++;
        IteratorContext context = _brain.Context;
        Vector2 origin = context.Body.Position;
        foreach (Player player in context.Players)
        {
            if (player.slatedForDeletetion || !ReferenceEquals(player.room, context.Room)) continue;
            bool created = !_byPlayer.TryGetValue(player, out PlayerObservation item);
            if (created) { item = new PlayerObservation(this, player); _players.Add(item); _byPlayer.Add(player, item); }
            if (item.Seen == SampleCount) continue;
            item.Seen = SampleCount;
            bool wasAlive = item.IsAlive, wasNear = item.IsNear;
            item.IsAlive = !player.dead; item.InShortcut = player.inShortcut;
            item.HasPosition = TryPosition(player, out Vector2 position); item.Position = position;
            item.Distance = item.HasPosition ? Vector2.Distance(origin, position) : float.PositiveInfinity;
            item.IsVisible = item.HasPosition && item.IsAlive && !item.InShortcut && item.Distance <= Profile.RetainRange &&
                (!Profile.RequireVisibility || context.Room.VisualContact(origin, position));
            item.IsNear = item.HasPosition && item.IsAlive && !item.InShortcut &&
                item.Distance <= (wasNear ? Profile.NearExitRange : Profile.NearRange);
            if (created) Emit(IteratorPlayerEventKind.Entered, item);
            else if (wasAlive != item.IsAlive) Emit(item.IsAlive ? IteratorPlayerEventKind.Revived : IteratorPlayerEventKind.Died, item);
            if (wasNear != item.IsNear) Emit(item.IsNear ? IteratorPlayerEventKind.Approached : IteratorPlayerEventKind.MovedAway, item);
        }
        NearestVisiblePlayer = null;
        for (int i = 0; i < _players.Count;)
        {
            PlayerObservation item = _players[i];
            if (item.Seen != SampleCount)
            {
                item.IsPresent = false;
                Emit(IteratorPlayerEventKind.Left, item); _byPlayer.Remove(item.Player); _retired.Add(item); _players.RemoveAt(i); continue;
            }
            if (CanObserve(item) && (NearestVisiblePlayer == null || item.Distance < NearestVisiblePlayer.Distance)) NearestVisiblePlayer = item;
            i++;
        }
    }
    private void Emit(IteratorPlayerEventKind kind, PlayerObservation item) => _events.Add(new IteratorPlayerEvent(kind, item));
    internal void FinishEvents()
    {
        foreach (PlayerObservation item in _retired) item.Clear();
        _retired.Clear(); _events.Clear();
    }
    internal void Release()
    {
        IsEnabled = false; NearestVisiblePlayer = null;
        foreach (PlayerObservation item in _players) item.Clear();
        _players.Clear(); _byPlayer.Clear(); FinishEvents();
    }
    private static bool TryPosition(Player player, out Vector2 position)
    {
        position = Vector2.zero;
        if (player.bodyChunks == null || player.bodyChunks.Length == 0 || player.bodyChunks[0] == null) return false;
        Vector2 point = player.bodyChunks[0].pos;
        if (float.IsNaN(point.x) || float.IsNaN(point.y) || Math.Abs(point.x) > 100000f || Math.Abs(point.y) > 100000f) return false;
        position = point; return true;
    }
    private sealed class PlayerIdentity : IEqualityComparer<Player>
    {
        internal static readonly PlayerIdentity Instance = new();
        public bool Equals(Player x, Player y) => ReferenceEquals(x, y);
        public int GetHashCode(Player obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
