// SPDX-FileCopyrightText: 2026 Goobstation
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.MobSwitcher;
using Content.Server.Mind;
using Content.Shared.Mind;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Goobstation.Server.MobSwitcher;

/// <summary>
/// Lets players maintain a list of mob entities and instantly transfer their mind between them.
/// Each player can only add mobs they are currently inhabiting, preventing cross-player hijacking.
/// </summary>
public sealed class MobSwitcherSystem : EntitySystem
{
    [Dependency] private readonly MindSystem _minds = default!;
    [Dependency] private readonly IPlayerManager _playerMgr = default!;

    // Per-player saved list: NetUserId → ordered entries
    private readonly Dictionary<NetUserId, List<SavedEntry>> _lists = new();

    private record SavedEntry(EntityUid Entity, string CharacterName);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<MobSwitcherAddCurrentEvent>(OnAddCurrent);
        SubscribeNetworkEvent<MobSwitcherSwitchEvent>(OnSwitch);
        SubscribeNetworkEvent<MobSwitcherRemoveEvent>(OnRemove);
        SubscribeNetworkEvent<MobSwitcherResetEvent>(OnReset);
        SubscribeNetworkEvent<MobSwitcherRequestStateEvent>(OnRequestState);
        SubscribeNetworkEvent<MobSwitcherCycleNextEvent>(OnCycleNext);
        SubscribeNetworkEvent<MobSwitcherCyclePrevEvent>(OnCyclePrev);
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private void OnAddCurrent(MobSwitcherAddCurrentEvent msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (session.AttachedEntity is not { Valid: true } attached)
            return;

        var list = GetList(session.UserId);

        // Avoid duplicates
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Entity == attached)
            {
                SendState(session);
                return;
            }
        }

        var charName = GetCharacterName(attached);
        list.Add(new SavedEntry(attached, charName));
        SendState(session);
    }

    private void OnSwitch(MobSwitcherSwitchEvent msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var target = GetEntity(msg.Target);

        if (!Exists(target))
        {
            PruneDeadEntries(session.UserId);
            SendState(session);
            return;
        }

        var list = GetList(session.UserId);

        // Security: only allow switching to an entity the player themselves added
        var allowed = false;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Entity == target) { allowed = true; break; }
        }
        if (!allowed)
        {
            Log.Warning($"Player {session.Name} tried to switch to entity {target} that is not in their mob list.");
            return;
        }

        if (!_minds.TryGetMind(session, out _, out _))
        {
            Log.Warning($"Player {session.Name} has no mind; cannot switch mob.");
            return;
        }

        // Use ControlMob instead of TransferTo directly so that:
        // 1. IgnoreBindSoulTag is applied – prevents soul-bound mobs from being gibbed when the mind leaves.
        // 2. MakeSentient is called on the target – restores InputMoverComponent and friends if anything
        //    stripped them while the mob was idle and unattended.
        // 3. The same safeguards used by the admin "Control Mob" verb are active, preventing ghost-role
        //    takeover race conditions that could leave the player's session unbound from the target entity.
        _minds.ControlMob(session.UserId, target);
        SendState(session);
    }

    private void OnRemove(MobSwitcherRemoveEvent msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var target = GetEntity(msg.Target);
        var list = GetList(session.UserId);
        list.RemoveAll(e => e.Entity == target);
        SendState(session);
    }

    private void OnReset(MobSwitcherResetEvent msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        _lists.Remove(session.UserId);
        SendState(session);
    }

    private void OnRequestState(MobSwitcherRequestStateEvent msg, EntitySessionEventArgs args)
    {
        SendState(args.SenderSession);
    }

    private void OnCycleNext(MobSwitcherCycleNextEvent msg, EntitySessionEventArgs args)
    {
        CycleMob(args.SenderSession, +1);
    }

    private void OnCyclePrev(MobSwitcherCyclePrevEvent msg, EntitySessionEventArgs args)
    {
        CycleMob(args.SenderSession, -1);
    }

    private void CycleMob(ICommonSession session, int direction)
    {
        PruneDeadEntries(session.UserId);
        var list = GetList(session.UserId);

        if (list.Count < 2)
            return;

        var current = session.AttachedEntity;
        var currentIndex = -1;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Entity == current)
            {
                currentIndex = i;
                break;
            }
        }

        // If the current mob isn't in the list, just go to the first entry
        var nextIndex = currentIndex == -1
            ? 0
            : (currentIndex + direction + list.Count) % list.Count;

        var target = list[nextIndex].Entity;

        if (!_minds.TryGetMind(session, out _, out _))
            return;

        _minds.ControlMob(session.UserId, target);
        SendState(session);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<SavedEntry> GetList(NetUserId userId)
    {
        if (!_lists.TryGetValue(userId, out var list))
        {
            list = new List<SavedEntry>();
            _lists[userId] = list;
        }
        return list;
    }

    private void SendState(ICommonSession session)
    {
        PruneDeadEntries(session.UserId);
        var list = GetList(session.UserId);
        var entries = new List<MobSwitcherEntry>(list.Count);

        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            var net = GetNetEntity(e.Entity);
            entries.Add(new MobSwitcherEntry
            {
                Entity = net,
                CharacterName = e.CharacterName,
                MobId = $"#{net.Id}",
            });
        }

        RaiseNetworkEvent(new MobSwitcherStateEvent { Entries = entries }, session.Channel);
    }

    private void PruneDeadEntries(NetUserId userId)
    {
        if (!_lists.TryGetValue(userId, out var list))
            return;
        list.RemoveAll(e => !Exists(e.Entity) || EntityManager.IsQueuedForDeletion(e.Entity));
    }

    private string GetCharacterName(EntityUid entity)
    {
        var meta = MetaData(entity);
        return string.IsNullOrWhiteSpace(meta.EntityName) ? "???" : meta.EntityName;
    }

}
