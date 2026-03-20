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

        if (!_minds.TryGetMind(session, out var mindId, out _))
        {
            Log.Warning($"Player {session.Name} has no mind; cannot switch mob.");
            return;
        }

        _minds.TransferTo(mindId, target, ghostCheckOverride: true, createGhost: false);
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
