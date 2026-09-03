using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.WorldLink;

internal sealed class MultiGateControllerRuntime : UpdatableAndDeletable
{
    private enum State { Idle, Opening, Open, Closing, SafeClosing, WarpPending }

    internal readonly MultiGateControllerData Data;
    internal PlacedObject Placed => _placed;

    private readonly PlacedObject _placed;
    private readonly List<MultiGatePortRuntime> _allPorts;
    private MultiGatePortRuntime _active;
    private State _state;
    private bool _inbound;
    private bool _retireAfterClose;
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
        if (!WorldLinkRoomRegistry.Enabled || room?.game == null || _placed == null || room.roomSettings?.placedObjects == null)
        {
            Destroy();
            return;
        }

        bool stillAuthored = room.roomSettings.placedObjects.Contains(_placed);
        if (!stillAuthored)
        {
            if (_active != null && _active.MechanicalFactor > 0.0001f)
            {
                _retireAfterClose = true;
                RequestSafeClose();
                UpdateSafeClosing();
            }
            else
            {
                Destroy();
            }
            return;
        }

        GateUnlockRequirements.PollHotReload(room.game.clock);

        bool primary = WorldLinkRoomRegistry.IsPrimaryController(room, this);
        bool activePortPhysicallyOwned = _active == null ||
            (Belongs(_active) && _active.Placed != null && _active.Placed.active &&
             room.roomSettings.placedObjects.Contains(_active.Placed));
        bool activeRouteStillAvailable = _active == null || _inbound || _active.Data.Enabled;

        if (!_placed.active || !primary || !activePortPhysicallyOwned || !activeRouteStillAvailable)
        {
            if (_active != null)
            {
                RequestSafeClose();
                UpdateSafeClosing();
            }
            else
            {
                ClearDeniedForOwnedPorts();
            }
            return;
        }

        switch (_state)
        {
            case State.Idle: UpdateIdle(); break;
            case State.Opening: UpdateOpening(); break;
            case State.Open: UpdateOpen(); break;
            case State.Closing: UpdateClosing(); break;
            case State.SafeClosing: UpdateSafeClosing(); break;
            case State.WarpPending: break;
        }
    }

    internal bool BeginInbound(MultiGatePortRuntime port)
    {
        // Directed routes are independent. The target port's outgoing Enabled flag must
        // not reject an arrival from a valid source route.
        if (port == null || !Belongs(port) || port.Placed?.active != true || _state != State.Idle) return false;

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
            if (!Belongs(port) || port.Placed?.active != true) continue;

            if (!port.Data.Enabled)
            {
                bool denied = port.AllProgressPlayersOnSide(interior: true) || port.AllProgressPlayersOnSide(interior: false);
                port.SetDenied(denied);
                continue;
            }

            port.SetDenied(false);
            if (port.AllProgressPlayersOnSide(interior: true)) inside ??= port;
            if (port.AllProgressPlayersOnSide(interior: false)) outside ??= port;
        }

        if (outside != null)
        {
            // Same-region native-node returns can arrive from the outside of this port.
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
        if (_active == null) { CompleteReset(); return; }
        if (_active.Placed?.active != true || !Belongs(_active) || (!_inbound && !_active.Data.Enabled))
        {
            RequestSafeClose();
            UpdateSafeClosing();
            return;
        }

        float speed = 1f / Mathf.Max(1f, _active.Data.OpenFrames);
        _active.SetMechanicalFactor(Mathf.Min(1f, _active.MechanicalFactor + speed));
        if (_active.MechanicalFactor >= 0.9999f)
        {
            _active.SetMechanicalFactor(1f);
            _state = State.Open;
            _openSafeCounter = 0;
        }
    }

    private void UpdateOpen()
    {
        if (_active == null) { CompleteReset(); return; }
        if (_active.Placed?.active != true || !Belongs(_active) || (!_inbound && !_active.Data.Enabled))
        {
            RequestSafeClose();
            UpdateSafeClosing();
            return;
        }

        _active.HoldCurrentPose();
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

        if (_openSafeCounter > 10 && (_active.AllPresentProgressPlayersOutsideOrGone() || !AnyProgressPlayerNearActivePort()))
        {
            _state = State.Closing;
        }
    }

    private void UpdateClosing() => AdvanceClosing(safeReset: false);
    private void UpdateSafeClosing() => AdvanceClosing(safeReset: true);

    private void AdvanceClosing(bool safeReset)
    {
        if (_active == null)
        {
            CompleteReset();
            return;
        }

        float speed = 1f / Mathf.Max(1f, _active.Data.CloseFrames);
        float nextMechanical = Mathf.Max(0f, _active.MechanicalFactor - speed);
        float nextOpen = _active.PreviewOpenFromMechanical(nextMechanical);

        if (nextOpen < _active.OpenFactor - 0.00001f && _active.WouldCrushPhysicalObject(nextOpen))
        {
            _active.HoldCurrentPose();
            return;
        }

        _active.SetMechanicalFactor(nextMechanical);
        if (_active.MechanicalFactor <= 0.0001f)
        {
            _active.SetMechanicalFactor(0f);
            CompleteReset();
        }
        else if (safeReset)
        {
            _active.SetDenied(false);
        }
    }

    private void RequestSafeClose()
    {
        if (_active == null || _state == State.WarpPending) return;
        _inbound = false;
        _armCounter = 0;
        _openSafeCounter = 0;
        _active.SetDenied(false);
        _state = State.SafeClosing;
    }

    private bool AnyProgressPlayerNearActivePort()
    {
        if (_active == null) return false;
        List<AbstractCreature> players = room.game.PlayersToProgressOrWin;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i]?.realizedCreature is Player p && p.room == room &&
                _active.IsWithinTransitEnvelope(p.mainBodyChunk.pos, 1.25f)) return true;
        }
        return false;
    }

    private bool Belongs(MultiGatePortRuntime port) =>
        port != null && string.Equals(port.Data.GateId, Data.GateId, StringComparison.OrdinalIgnoreCase);

    private void ClearDeniedForOwnedPorts()
    {
        for (int i = 0; i < _allPorts.Count; i++)
        {
            if (Belongs(_allPorts[i])) _allPorts[i].SetDenied(false);
        }
    }

    private void CompleteReset()
    {
        if (_active != null)
        {
            _active.SetDenied(false);
            if (_active.MechanicalFactor <= 0.0001f) _active.SetMechanicalFactor(0f);
        }

        _active = null;
        _inbound = false;
        _armCounter = 0;
        _openSafeCounter = 0;
        _state = State.Idle;

        if (_retireAfterClose)
        {
            _retireAfterClose = false;
            Destroy();
        }
    }
}
