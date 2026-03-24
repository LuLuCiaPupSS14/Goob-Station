// SPDX-FileCopyrightText: 2024 DEATHB4DEFEAT <77995199+DEATHB4DEFEAT@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 portfiend <109661617+portfiend@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <aiden@djkraz.com>
// SPDX-FileCopyrightText: 2025 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2025 deltanedas <39013340+deltanedas@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 deltanedas <@deltanedas:kde.org>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Actions;
using Content.Shared.Climbing.Events;
using Content.Shared._DV.Abilities;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Robust.Server.GameObjects;

namespace Content.Server._DV.Abilities;

public sealed partial class CrawlUnderObjectsSystem : SharedCrawlUnderObjectsSystem
{
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedActionsSystem _actionsSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CrawlUnderObjectsComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<CrawlUnderObjectsComponent, AttemptClimbEvent>(OnAttemptClimb);
    }

    private void OnInit(EntityUid uid, CrawlUnderObjectsComponent component, ComponentInit args)
    {
        if (component.ToggleHideAction != null)
            return;

        _actionsSystem.AddAction(uid, ref component.ToggleHideAction, component.ActionProto);
    }

    /// <summary>
    /// Server-authoritative blocking check. Called by the shared toggle handler.
    /// </summary>
    protected override bool TryBlockToggle(EntityUid uid, CrawlUnderObjectsComponent component)
    {
        // Base already checks IsOnClimbable (predicted on client).
        // We just add the server-only popup so the player knows why they were blocked.
        if (!base.TryBlockToggle(uid, component))
            return false;

        var msg = component.Enabled
            ? "crawl-under-objects-already-sneaking"
            : "crawl-under-objects-above-climbable";
        _popup.PopupClient(Loc.GetString(msg), uid, uid);
        return true;
    }

    protected override void OnCrawlingUpdated(EntityUid uid, CrawlUnderObjectsComponent component, CrawlingUpdatedEvent args)
    {
        base.OnCrawlingUpdated(uid, component, args); // shows PopupPredicted via shared base
        if (TryComp<AppearanceComponent>(uid, out var app))
            _appearance.SetData(uid, SneakMode.Enabled, args.Enabled, app);
    }

    private void OnAttemptClimb(EntityUid uid,
        CrawlUnderObjectsComponent component,
        AttemptClimbEvent args)
    {
        if (component.Enabled)
            args.Cancelled = true;
    }
}
