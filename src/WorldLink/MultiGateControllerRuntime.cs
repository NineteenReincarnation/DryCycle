using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

namespace DryCycle.WorldLink;

internal sealed class MultiGateControllerRuntime : UpdatableAndDeletable
{
    private enum State { Idle, Opening, Open, Closing, WarpPending }

    internal readonly MultiGateControllerData Data;
    internal PlacedObject Placed => _placed;
    private readonly PlacedObject _placed;
    private readonly List<MultiGatePortRuntime> _allPorts;
    private MultiGatePortRuntime _active;
    private State _state;
    private bool _inbound;
    private int _armCounter;
    private int _openSafeCounter;

    internal MultiGateControllerRuntime(Room room, PlacedObject placed, MultiGateControllerData data, List<MultiGatePortRuntime> allPorts)
    {
        this.room = room;
        _placed = placed;
        Data = data;
        _allPorts = allPorts;
    }

    public override void Update(bool eu)
    {
        base.Update(eu);
        if (!WorldLinkRoomRegistry.Enabled || room?.game == null || _placed == null || room.roomSettings?.placedObjects == null || !room.roomSettings.placedObjects.Contains(_placed))
        {
            SlateForDeletion();
            return;
        }
        if (!_placed.active)
        {
            if (_active != null) ForceReset();
            return;
        }
        GateUnlockRequirements.PollHotReload(room.game.clock);
        if (!WorldLinkRoomRegistry.IsPrimaryController(room, this))
        {
            if (_active != null) ForceReset();
            return;
        }

        // Membership is resolved from the live data every frame so mapper edits to
        // GateID immediately regroup ports without destroying/recreating room objects.
        if (_active != null && !Belongs(_active))
        {
            ForceReset();
        }

        switch (_state)
        {
            case State.Idle: UpdateIdle(); break;
            case State.Opening: UpdateOpening(); break;
            case State.Open: UpdateOpen(); break;
            case State.Closing: UpdateClosing(); break;
            case State.WarpPending: break;
        }
    }

    internal bool BeginInbound(MultiGatePortRuntime port)
    {
        if (port == null || !Belongs(port) || _state != State.Idle) return false;
        _active = port;
        _inbound = true;
        _armCounter = 0;
        _openSafeCounter = 0;
        _active.SetDenied(false);
        _state = State.Opening;
        return true;
    }

    private void UpdateIdle()
    {
        MultiGatePortRuntime inside = null;
        MultiGatePortRuntime outside = null;
        for (int i = 0; i < _allPorts.Count; i++)
        {
            MultiGatePortRuntime port = _allPorts[i];
            port.SetDenied(false);
            if (!Belongs(port) || !port.Data.Enabled) continue;
            if (port.AllProgressPlayersOnSide(interior: true)) inside ??= port;
            if (port.AllProgressPlayersOnSide(interior: false)) outside ??= port;
        }

        if (outside != null)
        {
            BeginInbound(outside);
            return;
        }

        if (inside == null)
        {
            _armCounter = 0;
            return;
        }

        if (inside.Data.TransitMode == WorldLinkTransitMode.DirectTransit)
        {
            inside.SetDenied(true);
            _armCounter = 0;
            return;
        }

        bool requirement = GateUnlockRequirements.Meets(room.game, room, inside.Address);
        inside.SetDenied(!requirement);
        if (!requirement || !inside.ProgressPlayersStandingStill())
        {
            _armCounter = 0;
            return;
        }

        _armCounter++;
        if (_armCounter < 60) return;

        _active = inside;
        _inbound = false;
        _armCounter = 0;
        GateUnlockRequirements.UnlockIfAllowed(room.game, _active.Address);
        room.game.manager.musicPlayer?.GateEvent();
        _state = State.Opening;
    }

    private void UpdateOpening()
    {
        if (_active == null) { ForceReset(); return; }
        float speed = 1f / Mathf.Max(1f, _active.Data.OpenFrames);
        _active.SetOpenFactor(Mathf.Min(1f, _active.OpenFactor + speed));
        if (_active.OpenFactor >= 0.9999f)
        {
            _active.SetOpenFactor(1f);
            _state = State.Open;
            _openSafeCounter = 0;
        }
    }

    private void UpdateOpen()
    {
        if (_active == null) { ForceReset(); return; }
        _active.SetOpenFactor(_active.OpenFactor);
        _openSafeCounter++;

        if (_inbound)
        {
            if (_openSafeCounter > 10 && (_active.AllProgressPlayersOnSide(interior: true) || !AnyProgressPlayerNearActivePort()))
            {
                _state = State.Closing;
            }
            return;
        }

        if (_active.Data.TransitMode == WorldLinkTransitMode.CrossRegion)
        {
            if (_active.AllProgressPlayersOnSide(interior: false))
            {
                if (WorldLinkTraversal.BeginCrossRegion(_active))
                {
                    _state = State.WarpPending;
                }
                else
                {
                    _active.SetDenied(true);
                    _state = State.Closing;
                }
            }
            return;
        }

        // VanillaNode: once every progression player has crossed to the node side or
        // already left this room through the native shortcut, the gate can close behind.
        if (_openSafeCounter > 10 && (_active.AllPresentProgressPlayersOutsideOrGone() || !AnyProgressPlayerNearActivePort()))
        {
            _state = State.Closing;
        }
    }

    private void UpdateClosing()
    {
        if (_active == null) { ForceReset(); return; }
        // Anti-crush is evaluated against the *next* aperture, not merely the current
        // slab. This prevents a fast-closing wide door from advancing one frame into a
        // BodyChunk before the obstruction guard notices it.
        float speed = 1f / Mathf.Max(1f, _active.Data.CloseFrames);
        float nextOpen = Mathf.Max(0f, _active.OpenFactor - speed);
        if (_active.WouldCrushPhysicalObject(nextOpen)) return;

        _active.SetOpenFactor(nextOpen);
        if (_active.OpenFactor <= 0.0001f)
        {
            _active.SetOpenFactor(0f);
            ForceReset();
        }
    }

    private bool AnyProgressPlayerNearActivePort()
    {
        if (_active == null) return false;
        List<AbstractCreature> players = room.game.PlayersToProgressOrWin;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i]?.realizedCreature is Player p && p.room == room && _active.IsWithinTransitEnvelope(p.mainBodyChunk.pos, 1.25f))
            {
                return true;
            }
        }
        return false;
    }

    private bool Belongs(MultiGatePortRuntime port) =>
        port != null && string.Equals(port.Data.GateId, Data.GateId, StringComparison.OrdinalIgnoreCase);

    private void ForceReset()
    {
        if (_active != null)
        {
            _active.SetDenied(false);
            if (_active.OpenFactor < 0.001f) _active.SetOpenFactor(0f);
        }
        _active = null;
        _inbound = false;
        _armCounter = 0;
        _openSafeCounter = 0;
        _state = State.Idle;
    }
}
