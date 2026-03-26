// SPDX-FileCopyrightText: 2021 Acruid <shatter66@gmail.com>
// SPDX-FileCopyrightText: 2021 Clyybber <darkmine956@gmail.com>
// SPDX-FileCopyrightText: 2021 Galactic Chimp <GalacticChimpanzee@gmail.com>
// SPDX-FileCopyrightText: 2021 Paul <ritter.paul1@googlemail.com>
// SPDX-FileCopyrightText: 2021 Vera Aguilera Puerto <6766154+Zumorica@users.noreply.github.com>
// SPDX-FileCopyrightText: 2021 Visne <39844191+Visne@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 Alex Evgrashin <aevgrashin@yandex.ru>
// SPDX-FileCopyrightText: 2022 Fishfish458 <47410468+Fishfish458@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 Francesco <frafonia@gmail.com>
// SPDX-FileCopyrightText: 2022 Jacob Tong <10494922+ShadowCommander@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 Leon Friedrich <60421075+ElectroJr@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 fishfish458 <fishfish458>
// SPDX-FileCopyrightText: 2022 keronshb <54602815+keronshb@users.noreply.github.com>
// SPDX-FileCopyrightText: 2022 metalgearsloth <metalgearsloth@gmail.com>
// SPDX-FileCopyrightText: 2023 DrSmugleaf <DrSmugleaf@users.noreply.github.com>
// SPDX-FileCopyrightText: 2023 Pieter-Jan Briers <pieterjan.briers@gmail.com>
// SPDX-FileCopyrightText: 2023 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 BombasterDS <115770678+BombasterDS@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2024 Tayrtahn <tayrtahn@gmail.com>
// SPDX-FileCopyrightText: 2024 whateverusername0 <whateveremail>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// HEAVILY EDITED
// if wizden ever does something to this system we're FUCKED
// regards.

using Content.Shared.Climbing.Components;
using Content.Shared.Climbing.Events;
using Content.Shared.Hands.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Rotation;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Standing;

public sealed class StandingStateSystem : EntitySystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    // If StandingCollisionLayer value is ever changed to more than one layer, the logic needs to be edited.
    public const int StandingCollisionLayer = (int) CollisionGroup.MidImpassable;

    private EntityQuery<ClimbingComponent> _climbingQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private readonly HashSet<Entity<ClimbableComponent>> _climbableBuffer = new();
    private readonly HashSet<EntityUid> _pendingFixtureRestores = new();
    private readonly List<EntityUid> _pendingRestoreBuffer = new();

    public override void Initialize()
    {
        base.Initialize();
        _climbingQuery = GetEntityQuery<ClimbingComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        SubscribeLocalEvent<StandingStateComponent, AttemptMobCollideEvent>(OnMobCollide);
        SubscribeLocalEvent<StandingStateComponent, AttemptMobTargetCollideEvent>(OnMobTargetCollide);
        SubscribeLocalEvent<StandingStateComponent, RefreshFrictionModifiersEvent>(OnRefreshFrictionModifiers);
        SubscribeLocalEvent<StandingStateComponent, TileFrictionEvent>(OnTileFriction);
        SubscribeLocalEvent<StandingStateComponent, StartClimbEvent>(OnStartClimb);
    }

    /// <summary>
    /// When climbing starts, strip MidImpassable from any fixture that still has it so the
    /// entity can rest on the climbable surface. Update() will restore it once they move clear.
    /// Fixtures already stripped by Down() are already tracked — nothing extra needed for them.
    /// </summary>
    private void OnStartClimb(Entity<StandingStateComponent> ent, ref StartClimbEvent args)
    {
        if (!TryComp(ent, out FixturesComponent? fixtures))
            return;

        var dirty = false;
        foreach (var (key, fixture) in fixtures.Fixtures)
        {
            if ((fixture.CollisionMask & StandingCollisionLayer) == 0)
                continue; // Already stripped (e.g. by Down()) — Down() already tracked it.
            _physics.SetCollisionMask(ent, key, fixture, fixture.CollisionMask & ~StandingCollisionLayer, fixtures);
            if (!ent.Comp.ChangedFixtures.Contains(key))
            {
                ent.Comp.ChangedFixtures.Add(key);
                dirty = true;
            }
        }
        if (dirty)
        {
            _pendingFixtureRestores.Add(ent);
            Dirty(ent, ent.Comp);
        }
    }

    /// <summary>
    /// Each frame: for standing entities with pending fixture restores, restore MidImpassable
    /// once they are no longer near any climbable. The pending set is typically 0-5 entities.
    /// </summary>
    public override void Update(float frameTime)
    {
        if (_pendingFixtureRestores.Count == 0)
            return;

        _pendingRestoreBuffer.Clear();
        _pendingRestoreBuffer.AddRange(_pendingFixtureRestores);

        foreach (var uid in _pendingRestoreBuffer)
        {
            if (!TryComp<StandingStateComponent>(uid, out var standing)
                || !TryComp<FixturesComponent>(uid, out var fixtures))
            {
                _pendingFixtureRestores.Remove(uid);
                continue;
            }

            if (!standing.Standing || standing.ChangedFixtures.Count == 0)
            {
                _pendingFixtureRestores.Remove(uid);
                continue;
            }

            // Don't restore while climbing — entity must pass through climbable surfaces.
            if (_climbingQuery.TryGetComponent(uid, out var climbing) && climbing.IsClimbing)
                continue;
            if (IsOnClimbable(uid))
                continue;

            foreach (var key in standing.ChangedFixtures)
            {
                if (fixtures.Fixtures.TryGetValue(key, out var fixture))
                    _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask | StandingCollisionLayer, fixtures);
            }
            standing.ChangedFixtures.Clear();
            _pendingFixtureRestores.Remove(uid);
            Dirty(uid, standing);
        }
    }

    public bool IsOnClimbable(EntityUid uid, float range = 0.4f)
    {
        _climbableBuffer.Clear();
        _lookup.GetEntitiesInRange(_xformQuery.GetComponent(uid).Coordinates, range, _climbableBuffer);
        return _climbableBuffer.Count > 0;
    }

    /// <summary>
    /// Remove a fixture key from the pending MidImpassable restore list, if present.
    /// Returns true if the key was pending (meaning MidImpassable should be included
    /// in the caller's saved original mask).
    /// </summary>
    public bool ClaimPendingFixtureRestore(EntityUid uid, string fixtureKey, StandingStateComponent? standing = null)
    {
        if (!Resolve(uid, ref standing, false))
            return false;

        if (!standing.ChangedFixtures.Remove(fixtureKey))
            return false;

        if (standing.ChangedFixtures.Count == 0)
            _pendingFixtureRestores.Remove(uid);

        Dirty(uid, standing);
        return true;
    }

    /// <summary>
    /// Register fixture keys so that StandingStateSystem.Update() will restore their MidImpassable
    /// once the entity moves clear of any climbable. Used by systems that strip MidImpassable
    /// (e.g. FlightSystem on landing) to avoid getting stuck inside table surfaces.
    /// </summary>
    public void DeferMidImpassableRestore(EntityUid uid, IEnumerable<string> fixtureKeys, StandingStateComponent? standing = null)
    {
        if (!Resolve(uid, ref standing))
            return;
        var dirty = false;
        foreach (var key in fixtureKeys)
        {
            if (standing.ChangedFixtures.Contains(key))
                continue;
            standing.ChangedFixtures.Add(key);
            dirty = true;
        }
        if (dirty)
        {
            _pendingFixtureRestores.Add(uid);
            Dirty(uid, standing);
        }
    }

    private void OnMobTargetCollide(Entity<StandingStateComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        if (!ent.Comp.Standing)
        {
            args.Cancelled = true;
        }
    }

    private void OnMobCollide(Entity<StandingStateComponent> ent, ref AttemptMobCollideEvent args)
    {
        if (!ent.Comp.Standing)
        {
            args.Cancelled = true;
        }
    }

    private void OnRefreshFrictionModifiers(Entity<StandingStateComponent> entity, ref RefreshFrictionModifiersEvent args)
    {
        if (entity.Comp.Standing)
            return;

        args.ModifyFriction(entity.Comp.DownFrictionMod);
        args.ModifyAcceleration(entity.Comp.DownFrictionMod);
    }

    private void OnTileFriction(Entity<StandingStateComponent> entity, ref TileFrictionEvent args)
    {
        if (!entity.Comp.Standing)
            args.Modifier *= entity.Comp.DownFrictionMod;
    }

    public bool IsDown(EntityUid uid, StandingStateComponent? standingState = null)
    {
        if (!Resolve(uid, ref standingState, false))
            return false;

        return !standingState.Standing;
    }

    public bool Down(EntityUid uid,
        bool playSound = true,
        bool dropHeldItems = true,
        bool force = false,
        StandingStateComponent? standingState = null,
        AppearanceComponent? appearance = null,
        HandsComponent? hands = null)
    {
        // TODO: This should actually log missing comps...
        if (!Resolve(uid, ref standingState, false))
            return false;

        // Optional component.
        Resolve(uid, ref appearance, ref hands, false);

        if (!standingState.Standing)
            return true;

        // This is just to avoid most callers doing this manually saving boilerplate
        // 99% of the time you'll want to drop items but in some scenarios (e.g. buckling) you don't want to.
        // We do this BEFORE downing because something like buckle may be blocking downing but we want to drop hand items anyway
        // and ultimately this is just to avoid boilerplate in Down callers + keep their behavior consistent.
        if (dropHeldItems && hands != null)
        {
            var ev = new DropHandItemsEvent();
            RaiseLocalEvent(uid, ref ev, false);
        }

        if (!force)
        {
            var msg = new DownAttemptEvent();
            RaiseLocalEvent(uid, msg, false);

            if (msg.Cancelled)
                return false;
        }

        standingState.Standing = false;
        Dirty(uid, standingState);
        RaiseLocalEvent(uid, new DownedEvent(), false);

        // Seemed like the best place to put it
        _appearance.SetData(uid, RotationVisuals.RotationState, RotationState.Horizontal, appearance);

        // Change collision masks to allow going under certain entities like flaps and tables
        if (TryComp(uid, out FixturesComponent? fixtureComponent))
        {
            foreach (var (key, fixture) in fixtureComponent.Fixtures)
            {
                if ((fixture.CollisionMask & StandingCollisionLayer) == 0)
                    continue;

                if (!standingState.ChangedFixtures.Contains(key))
                    standingState.ChangedFixtures.Add(key);
                _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask & ~StandingCollisionLayer, manager: fixtureComponent);
            }

            if (standingState.ChangedFixtures.Count > 0)
                _pendingFixtureRestores.Add(uid);
        }

        // check if component was just added or streamed to client
        // if true, no need to play sound - mob was down before player could seen that
        if (standingState.LifeStage <= ComponentLifeStage.Starting)
            return true;

        if (playSound)
        {
            _audio.PlayPredicted(standingState.DownSound, uid, uid);
        }

        return true;
    }

    public bool Stand(EntityUid uid,
        StandingStateComponent? standingState = null,
        AppearanceComponent? appearance = null,
        bool force = false)
    {
        // TODO: This should actually log missing comps...
        if (!Resolve(uid, ref standingState, false))
            return false;

        // Optional component.
        Resolve(uid, ref appearance, false);

        if (standingState.Standing)
            return true;

        if (!force)
        {
            var msg = new StandAttemptEvent();
            RaiseLocalEvent(uid, msg, false);

            if (msg.Cancelled)
                return false;
        }

        standingState.Standing = true;
        RaiseLocalEvent(uid, new StoodEvent(), false);

        _appearance.SetData(uid, RotationVisuals.RotationState, RotationState.Vertical, appearance);

        // Restore MidImpassable — but if the entity is on a climbable surface (e.g. they
        // were knocked down onto a table), defer the restore to Update() so they don't clip
        // into the table. This matches how FlightSystem.EnableCollisionMasks handles landing.
        if (TryComp(uid, out FixturesComponent? fixtureComponent))
        {
            if (standingState.ChangedFixtures.Count > 0 && IsOnClimbable(uid))
            {
                // Keep ChangedFixtures populated; Update() will restore once they move clear.
                _pendingFixtureRestores.Add(uid);
            }
            else
            {
                foreach (var key in standingState.ChangedFixtures)
                {
                    if (fixtureComponent.Fixtures.TryGetValue(key, out var fixture))
                        _physics.SetCollisionMask(uid, key, fixture, fixture.CollisionMask | StandingCollisionLayer, fixtureComponent);
                }
                standingState.ChangedFixtures.Clear();
                _pendingFixtureRestores.Remove(uid);
            }
        }
        else
        {
            standingState.ChangedFixtures.Clear();
            _pendingFixtureRestores.Remove(uid);
        }

        Dirty(uid, standingState);

        return true;
    }
}

[ByRefEvent]
public record struct DropHandItemsEvent(bool Handled = false); // Goob edit

/// <summary>
/// Subscribe if you can potentially block a down attempt.
/// </summary>
public sealed class DownAttemptEvent : CancellableEntityEventArgs
{
}

/// <summary>
/// Subscribe if you can potentially block a stand attempt.
/// </summary>
public sealed class StandAttemptEvent : CancellableEntityEventArgs
{
}

/// <summary>
/// Raised when an entity becomes standing
/// </summary>
public sealed class StoodEvent : EntityEventArgs
{
}

/// <summary>
/// Raised when an entity is not standing
/// </summary>
public sealed class DownedEvent : EntityEventArgs
{
}

/// <summary>
/// Raised after an entity falls down.
/// </summary>
public sealed class FellDownEvent : EntityEventArgs
{
    public EntityUid Uid { get; }

    public FellDownEvent(EntityUid uid)
    {
        Uid = uid;
    }
}

/// <summary>
/// Raised on the entity being thrown due to the holder falling down.
/// </summary>
[ByRefEvent]
public record struct FellDownThrowAttemptEvent(EntityUid Thrower, bool Cancelled = false);


