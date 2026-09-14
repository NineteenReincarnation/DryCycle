using System;
using System.Collections.Generic;

namespace DryCycle.Iterators;

/// <summary>一个实例的统一上下文。销毁回调结束后释放游戏引用，定义和日志仍可查询。</summary>
public sealed class IteratorContext
{
    private readonly List<Player> _players = new();

    internal IteratorContext(IteratorDescriptor descriptor, Room room)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Room = room ?? throw new ArgumentNullException(nameof(room));
        World = room.world;
        Game = room.game;
        Players = _players.AsReadOnly();
        Logger = new IteratorLogger(descriptor.ID).ForModule("Runtime").ForPhase("RuntimeCreated");
    }

    public IteratorID ID => Descriptor.ID;
    public IteratorDescriptor Descriptor { get; }
    public IteratorRuntime Runtime { get; private set; }
    public IteratorBody Body => Runtime?.Body;
    public IteratorArm Arm => Runtime?.Arm;
    public IteratorGraphics Graphics => Runtime?.Graphics;
    public IteratorBrain Brain => Runtime?.Brain;
    public Oracle Oracle { get; private set; }
    public Room Room { get; private set; }
    public World World { get; private set; }
    public RainWorldGame Game { get; private set; }
    public StoryGameSession StorySession => Game?.session as StoryGameSession;

    /// <summary>本房间已实体化的 Session 玩家，只读实时视图；初始化及每次 Update 前刷新，销毁时清空。</summary>
    public IReadOnlyList<Player> Players { get; }

    /// <summary>优先选择存活且不在捷径中的玩家，其次存活玩家，最后选择仍在房间中的玩家。</summary>
    public Player PrimaryPlayer { get; private set; }

    /// <summary>自动标记当前 Runtime 回调阶段的 Logger。</summary>
    public IteratorLogger Logger { get; internal set; }

    internal void BindRuntime(IteratorRuntime runtime)
    {
        if (Runtime != null || runtime == null || !ReferenceEquals(runtime.Context, this))
            throw new InvalidOperationException($"IteratorFramework: invalid Runtime binding for '{ID}'.");
        Runtime = runtime;
    }

    internal void BindOracle(Oracle oracle)
    {
        if (Oracle != null || oracle == null || !ReferenceEquals(oracle.room, Room))
            throw new InvalidOperationException($"IteratorFramework: invalid Oracle binding for '{ID}'.");
        Oracle = oracle;
    }

    internal void RefreshPlayers()
    {
        _players.Clear();
        PrimaryPlayer = null;
        List<AbstractCreature> players = Game?.session?.Players;
        if (Room == null || players == null)
            return;

        int bestPriority = -1;
        foreach (AbstractCreature abstractPlayer in players)
        {
            if (abstractPlayer?.realizedCreature is not Player player || !ReferenceEquals(player.room, Room))
                continue;
            _players.Add(player);
            int priority = player.dead ? 0 : player.inShortcut ? 1 : 2;
            if (priority > bestPriority)
            {
                PrimaryPlayer = player;
                bestPriority = priority;
            }
        }
    }

    internal void ReleaseGameReferences()
    {
        _players.Clear();
        PrimaryPlayer = null;
        Oracle = null;
        Room = null;
        World = null;
        Game = null;
    }
}
