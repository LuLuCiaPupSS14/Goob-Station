// SPDX-FileCopyrightText: 2026 Goobstation
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Goobstation.Shared.MobSwitcher;

/// <summary>
/// A single entry in a player's mob switcher list.
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherEntry
{
    public NetEntity Entity { get; init; }

    /// <summary>Character name shown as the primary label.</summary>
    public string CharacterName { get; init; } = string.Empty;

    /// <summary>Friendly mob entity ID shown beneath the character name.</summary>
    public string MobId { get; init; } = string.Empty;
}

/// <summary>
/// Client → Server: add the player's currently-inhabited mob to their switcher list.
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherAddCurrentEvent : EntityEventArgs;

/// <summary>
/// Client → Server: transfer the player's mind to the given saved mob.
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherSwitchEvent : EntityEventArgs
{
    public NetEntity Target { get; init; }
}

/// <summary>
/// Client → Server: remove the given mob from the player's list.
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherRemoveEvent : EntityEventArgs
{
    public NetEntity Target { get; init; }
}

/// <summary>
/// Client → Server: clear the entire list.
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherResetEvent : EntityEventArgs;

/// <summary>
/// Client → Server: cycle to the next mob in the list (wraps around).
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherCycleNextEvent : EntityEventArgs;

/// <summary>
/// Client → Server: cycle to the previous mob in the list (wraps around).
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherCyclePrevEvent : EntityEventArgs;

/// <summary>
/// Client → Server: request the current list state (e.g. when reopening the window).
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherRequestStateEvent : EntityEventArgs;

/// <summary>
/// Server → Client: pushed whenever the player's list changes.
/// </summary>
[Serializable, NetSerializable]
public sealed class MobSwitcherStateEvent : EntityEventArgs
{
    public List<MobSwitcherEntry> Entries { get; init; } = new();
}
