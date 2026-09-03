using System;
using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.WorldLink;

internal sealed class MultiGateControllerRuntime : UpdatableAndDeletable
{
    private enum State { Idle, Opening, Open, Closing, SafeClosing, WarpPending }
    private const int WarpPendingTimeoutFrames = 1800;

    internal readonly MultiGateControllerData Data;
    internal PlacedObject Placed => _placed;

    private readonly PlacedObject _placed;
    private readonly List<MultiGatePortRuntime> _allPorts;
    private MultiGatePortRuntime _active;
    private MultiGatePortRuntime _armingPort;
    private State _state;
    private bool _inbound;
    private bool _retireAfterClose;
    private int _armCounter;
    private int _openSafeCounter;
    private int _warpPendingCounter;

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
            ResetArming();
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
            case State.WarpPending: UpdateWarpPending(); break;
        }
    }

    internal bool BeginInbound(MultiGatePortRuntime port)
    {
        // Inbound is an explicit authorization path. Cross-region traversal calls this
        // from its successful world-load callback; same-region traversal calls it only
        // after Player.SpitOutOfShortCut reports the exact configured VanillaNode.
        if (port == null || !Belongs(port) || port.Placed?.active != true || _state != State.Idle ||
            !WorldLinkRoomRegistry.IsUniquePortAddress(room, port))
        {
            return false;
        }

        _active = port;
        _armingPort = null;
        _inbound = true;
        _armCounter = 0;
        _openSafeCounter = 0;
        _warpPendingCounter = 0;
        _active.SetDenied(false);
        _state = State.Opening;
        return true;
    }

    private void UpdateIdle()
    {
        MultiGatePortRuntime inside = null;
        bool ambiguous = false;

        for (int i = 0; i < _allPorts.Count; i++)
        {
            MultiGatePortRuntime port = _allPorts[i];
            if (!Belongs(port) || port.Placed?.active != true) continue;

            bool inZone = port.AllProgressPlayersOnSide(interior: true);

            if (!WorldLinkRoomRegistry.IsUniquePortAddress(room, port))
            {
                if (inZone) port.SetDenied(true);
                continue;
            }

            if (!port.Data.Enabled)
            {
                port.SetDenied(inZone);
                continue;
            }

            port.SetDenied(false);
            if (!inZone) continue;

            if (inside == null)
            {
                inside = port;
            }
            else if (!ReferenceEquals(inside, port))
            {
                inside.SetDenied(true);
                port.SetDenied(true);
                ambiguous = true;
            }
        }

        // Overlapping activation envelopes must never select a route by authored-list
        // order. A multi-port hub either resolves to one unambiguous outgoing port or
        // stays shut. Standing behind a port is not inbound authorization.
        if (ambiguous)
        {
            ResetArming();
            return;
        }

        if (inside == null)
        {
            ResetArming();
            return;
        }

        if (!inside.CanArmOutgoing())
        {
            inside.SetDenied(true);
            ResetArming();
            return;
        }

        // The 60-frame arming delay belongs to a specific directed port. Moving from
        // one overlapping/adjacent activation zone to another must never carry progress.
        if (!ReferenceEquals(_armingPort, inside))
        {
            _armingPort = inside;
            _armCounter = 0;
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

        // A cross-region endpoint is part of the route's validity, not a late warp
        // detail. Resolve it before writing vanilla-like permanent gate unlock state;
        // otherwise a typo could permanently unlock a door that can never traverse.
        if (inside.Data.TransitMode == WorldLinkTransitMode.CrossRegion &&
            !WorldLinkTraversal.CanResolveCrossRegionDestination(inside))
        {
            inside.SetDenied(true);
            ResetArming();
            return;
        }

        _active = inside;
        _armingPort = null;
        _inbound = false;
        _armCounter = 0;
        _warpPendingCounter = 0;
        GateUnlockRequirements.UnlockIfAllowed(room.game, _active.Address);
        room.game.manager.musicPlayer?.GateEvent();
        _state = State.Opening;
    }

    private void UpdateOpening()
    {
        if (_active == null) { CompleteReset(); return; }
        if (_active.Placed?.active != true || !Belongs(_active) ||
            !WorldLinkRoomRegistry.IsUniquePortAddress(room, _active) || (!_inbound && !_active.Data.Enabled))
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
        if (_active.Placed?.active != true || !Belongs(_active) ||
            !WorldLinkRoomRegistry.IsUniquePortAddress(room, _active) || (!_inbound && !_active.Data.Enabled))
        {
            RequestSafeClose();
            UpdateSafeClosing();
            return;
        }

        _active.HoldCurrentPose();
        _openSafeCounter++;

        if (_inbound)
        {
            // In co-op, shortcut arrivals are emitted one player at a time. Do not use
            // "nobody is near the gate" as a close condition: the first player can move
            // away before another player's vessel is spat into this room. Close only
            // after every progression player exists in this room and every BodyChunk is
            // safely beyond the physical panel on the interior side.
            if (_openSafeCounter > 10 && _active.AllProgressPlayersClearInside())
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
                    _warpPendingCounter = 0;
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

    private void UpdateWarpPending()
    {
        if (_active == null)
        {
            CompleteReset();
            return;
        }

        _active.HoldCurrentPose();
        _warpPendingCounter++;
        if (_warpPendingCounter <= WarpPendingTimeoutFrames) return;

        Plugin.Logger?.LogError($"WorldLink: cross-region warp from {_active.Address} did not hand off after {WarpPendingTimeoutFrames} frames. Closing the source gate fail-safe.");
        _state = State.SafeClosing;
        UpdateSafeClosing();
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
        if (_active == null) return;
        _inbound = false;
        ResetArming();
        _openSafeCounter = 0;
        _warpPendingCounter = 0;
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

    private void ResetArming()
    {
        _armingPort = null;
        _armCounter = 0;
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
        ResetArming();
        _openSafeCounter = 0;
        _warpPendingCounter = 0;
        _state = State.Idle;

        if (_retireAfterClose)
        {
            _retireAfterClose = false;
            Destroy();
        }
    }
}
