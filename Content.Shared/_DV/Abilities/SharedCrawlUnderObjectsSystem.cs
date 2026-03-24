// SPDX-FileCopyrightText: 2024 DEATHB4DEFEAT <77995199+DEATHB4DEFEAT@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 portfiend <109661617+portfiend@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <aiden@djkraz.com>
// SPDX-FileCopyrightText: 2025 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2025 deltanedas <39013340+deltanedas@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 deltanedas <@deltanedas:kde.org>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Climbing.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._DV.Abilities;
public abstract class SharedCrawlUnderObjectsSystem : EntitySystem
{
    [Dependency] protected readonly MovementSpeedModifierSystem _movespeed = default!;
    [Dependency] protected readonly SharedPopupSystem _popup = default!;
    [Dependency] protected readonly SharedPhysicsSystem _physics = default!;
    [Dependency] protected readonly StandingStateSystem _standing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CrawlUnderObjectsComponent, CrawlingUpdatedEvent>(OnCrawlingUpdated);
        SubscribeLocalEvent<CrawlUnderObjectsComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovespeed);
        SubscribeLocalEvent<CrawlUnderObjectsComponent, ToggleCrawlingStateEvent>(OnToggleCrawling);
        SubscribeLocalEvent<CrawlUnderObjectsComponent, AfterAutoHandleStateEvent>(OnHandleState);
    }

    private void OnRefreshMovespeed(EntityUid uid, CrawlUnderObjectsComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        if (component.Enabled)
            args.ModifySpeed(component.SneakSpeedModifier, component.SneakSpeedModifier);
    }

    protected virtual void OnCrawlingUpdated(EntityUid uid,
        CrawlUnderObjectsComponent component,
        CrawlingUpdatedEvent args)
    {
        // Use PopupPredicted so the client sees this immediately on key press (predicted),
        // and the server's confirmation is deduplicated — never shows twice.
        var msg = args.Enabled
            ? "crawl-under-objects-toggle-on"
            : "crawl-under-objects-toggle-off";
        _popup.PopupPredicted(Loc.GetString(msg), uid, uid);
    }

    // Predicted on both client and server. Server overrides TryBlockToggle to add server-authoritative checks.
    private void OnToggleCrawling(EntityUid uid, CrawlUnderObjectsComponent component, ToggleCrawlingStateEvent args)
    {
        if (args.Handled)
            return;

        if (TryBlockToggle(uid, component))
        {
            args.Handled = true;
            return;
        }

        var result = component.Enabled
            ? DisableSneakMode(uid, component)
            : EnableSneakMode(uid, component);

        if (result)
        {
            _movespeed.RefreshMovementSpeedModifiers(uid);
            RaiseLocalEvent(uid, new CrawlingUpdatedEvent(component.Enabled));
        }
        args.Handled = true;
    }

    /// <summary>
    /// Resync fixture masks immediately when the server sends a state correction.
    /// Without this, there is a one-frame window between rollback and prediction-replay
    /// where the physics fixtures don't match the component state, causing a visible jitter.
    /// </summary>
    private void OnHandleState(EntityUid uid, CrawlUnderObjectsComponent component, ref AfterAutoHandleStateEvent args)
    {
        if (!TryComp(uid, out FixturesComponent? fixtures))
            return;

        if (component.Enabled)
        {
            // Sneak is on — ensure the correct layers are stripped.
            foreach (var (key, originalMask) in component.ChangedFixtures)
            {
                if (!fixtures.Fixtures.TryGetValue(key, out var fixture))
                    continue;
                var sneakMask = originalMask & ~StandingStateSystem.StandingCollisionLayer;
                if (component.CanCrawlUnderDoors)
                    sneakMask &= (int) ~CollisionGroup.HighImpassable;
                if (fixture.CollisionMask != sneakMask)
                    _physics.SetCollisionMask(uid, key, fixture, sneakMask, manager: fixtures);
            }
        }
        else
        {
            // Sneak is off — restore original masks, but respect current prone state:
            // don't add MidImpassable back if the entity is currently down (StandingState owns it).
            var entityDown = _standing.IsDown(uid);
            foreach (var (key, originalMask) in component.ChangedFixtures)
            {
                if (!fixtures.Fixtures.TryGetValue(key, out var fixture))
                    continue;
                var restored = entityDown
                    ? originalMask & ~StandingStateSystem.StandingCollisionLayer
                    : originalMask;
                if (fixture.CollisionMask != restored)
                    _physics.SetCollisionMask(uid, key, fixture, restored, fixtures);
            }
        }
    }

    /// <summary>
    /// Override on the server to perform authoritative blocking checks before the toggle runs.
    /// Return true to block the toggle (already shows popup).
    /// Base implementation blocks near climbables (predicted on client too).
    /// </summary>
    protected virtual bool TryBlockToggle(EntityUid uid, CrawlUnderObjectsComponent component)
    {
        // Checked on both client and server so the client predicts the block correctly
        // and avoids misprediction rubber-band when near a table.
        return _standing.IsOnClimbable(uid);
    }

    private bool EnableSneakMode(EntityUid uid, CrawlUnderObjectsComponent component)
    {
        if (component.Enabled)
            return false;
        if (TryComp<ClimbingComponent>(uid, out var climbing) && climbing.IsClimbing)
            return false;

        component.Enabled = true;
        Dirty(uid, component);

        if (!TryComp(uid, out FixturesComponent? fixtures))
            return true;

        // Resolve StandingState once upfront. Only attempt to claim pending fixture restores
        // if there are actually any — the common case (entity is standing normally) has none.
        var standingComp = CompOrNull<StandingStateComponent>(uid);
        var hasStandingPending = standingComp is { ChangedFixtures.Count: > 0 };

        foreach (var (key, fixture) in fixtures.Fixtures)
        {
            // Strip MidImpassable so the entity can pass under tables.
            // Only strip HighImpassable (doors/airlocks) if the component explicitly allows it.
            var newMask = fixture.CollisionMask & ~StandingStateSystem.StandingCollisionLayer;
            if (component.CanCrawlUnderDoors)
                newMask &= (int) ~CollisionGroup.HighImpassable;

            // Build the "true original" mask (before any other system modified it).
            // If the entity is currently prone, StandingStateSystem may have already stripped
            // MidImpassable and is holding a pending restore for when they stand up.
            // Claim that responsibility so Stand() doesn't add MidImpassable back while we
            // are still sneaking.
            var originalMask = fixture.CollisionMask;
            var claimedFromStanding = hasStandingPending && _standing.ClaimPendingFixtureRestore(uid, key, standingComp);
            if (claimedFromStanding)
                originalMask |= StandingStateSystem.StandingCollisionLayer; // true original had it

            if (fixture.CollisionMask == newMask && !claimedFromStanding)
                continue;

            component.ChangedFixtures.Add((key, originalMask));
            if (fixture.CollisionMask != newMask)
                _physics.SetCollisionMask(uid, key, fixture, newMask, manager: fixtures);
        }
        return true;
    }

    private bool DisableSneakMode(EntityUid uid, CrawlUnderObjectsComponent component)
    {
        if (!component.Enabled)
            return false;
        if (TryComp<ClimbingComponent>(uid, out var climbing) && climbing.IsClimbing)
            return false;

        component.Enabled = false;
        Dirty(uid, component);

        if (!TryComp(uid, out FixturesComponent? fixtures))
            return true;

        var entityDown = _standing.IsDown(uid);
        List<string>? fixturesToDefer = null;

        foreach (var (key, originalMask) in component.ChangedFixtures)
        {
            if (!fixtures.Fixtures.TryGetValue(key, out var fixture))
                continue;

            int restored;
            if (entityDown)
            {
                // Entity is prone: restore everything except MidImpassable.
                // StandingStateSystem will handle restoring MidImpassable when they stand up.
                restored = originalMask & ~StandingStateSystem.StandingCollisionLayer;

                // If the original mask had MidImpassable, hand that restore-on-stand
                // responsibility back to StandingStateSystem now.
                if ((originalMask & StandingStateSystem.StandingCollisionLayer) != 0)
                {
                    fixturesToDefer ??= new List<string>();
                    fixturesToDefer.Add(key);
                }
            }
            else
            {
                // Entity is standing: fully restore the original mask.
                restored = originalMask;
            }

            _physics.SetCollisionMask(uid, key, fixture, restored, fixtures);
        }
        component.ChangedFixtures.Clear();

        if (fixturesToDefer?.Count > 0)
            _standing.DeferMidImpassableRestore(uid, fixturesToDefer);

        return true;
    }
}