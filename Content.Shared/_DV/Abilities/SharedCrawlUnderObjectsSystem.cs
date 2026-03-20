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
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

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
            // Ensure sneak masks are applied — if ChangedFixtures is populated, masks should already
            // be correct, but re-apply anything that drifted.
            foreach (var (key, originalMask) in component.ChangedFixtures)
            {
                if (!fixtures.Fixtures.TryGetValue(key, out var fixture))
                    continue;
                var sneakMask = originalMask & (int) ~CollisionGroup.HighImpassable;
                // Preserve the current MidImpassable state — StandingStateSystem may have
                // stripped it (e.g. entity downed while sneaking). Don't fight with it.
                sneakMask = (sneakMask & ~StandingStateSystem.StandingCollisionLayer)
                            | (fixture.CollisionMask & StandingStateSystem.StandingCollisionLayer);
                if (fixture.CollisionMask != sneakMask)
                    _physics.SetCollisionMask(uid, key, fixture, sneakMask, manager: fixtures);
            }
        }
        else
        {
            // Sneak is off — ensure any fixtures whose original masks are recorded are properly restored.
            foreach (var (key, originalMask) in component.ChangedFixtures)
            {
                if (!fixtures.Fixtures.TryGetValue(key, out var fixture))
                    continue;
                var restored = (originalMask & ~StandingStateSystem.StandingCollisionLayer)
                               | (fixture.CollisionMask & StandingStateSystem.StandingCollisionLayer);
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

        foreach (var (key, fixture) in fixtures.Fixtures)
        {
            // Only strip HighImpassable — do NOT add InteractImpassable.
            // Adding InteractImpassable to the mask would cause new contacts with any entity
            // whose collision layer includes InteractImpassable (e.g. DamageContactsComponent
            // objects like barbed wire), resulting in unexpected contact damage.
            var newMask = fixture.CollisionMask & (int) ~CollisionGroup.HighImpassable;
            if (fixture.CollisionMask == newMask)
                continue;
            component.ChangedFixtures.Add((key, fixture.CollisionMask));
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

        foreach (var (key, originalMask) in component.ChangedFixtures)
        {
            if (!fixtures.Fixtures.TryGetValue(key, out var fixture))
                continue;
            // Preserve whatever MidImpassable state StandingStateSystem currently has.
            var restored = (originalMask & ~StandingStateSystem.StandingCollisionLayer)
                           | (fixture.CollisionMask & StandingStateSystem.StandingCollisionLayer);
            _physics.SetCollisionMask(uid, key, fixture, restored, fixtures);
        }
        component.ChangedFixtures.Clear();
        return true;
    }
}