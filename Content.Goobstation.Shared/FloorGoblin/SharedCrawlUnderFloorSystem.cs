// SPDX-FileCopyrightText: 2025 Evaisa <mail@evaisa.dev>
// SPDX-FileCopyrightText: 2025 RichardBlonski <48651647+RichardBlonski@users.noreply.github.com>
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._DV.Abilities;
using Content.Shared._Starlight.VentCrawling;
using Content.Shared.Climbing.Components;
using Content.Shared.Climbing.Events;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Content.Shared.Actions;
using Content.Shared.Mobs.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Conveyor;

namespace Content.Goobstation.Shared.FloorGoblin;

public abstract class SharedCrawlUnderFloorSystem : EntitySystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tileManager = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedActionsSystem _actionsSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly TileSystem _tile = default!;
    [Dependency] private readonly SharedStealthSystem _stealth = default!;

    private EntityQuery<MobStateComponent> _mobStateQuery;
    private EntityQuery<AirlockComponent> _airlockQuery;
    private EntityQuery<ConveyorComponent> _conveyorQuery;

    public override void Initialize()
    {
        base.Initialize();

        _mobStateQuery = GetEntityQuery<MobStateComponent>();
        _airlockQuery = GetEntityQuery<AirlockComponent>();
        _conveyorQuery = GetEntityQuery<ConveyorComponent>();

        SubscribeLocalEvent<CrawlUnderFloorComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<CrawlUnderFloorComponent, ToggleCrawlingStateEvent>(OnAbilityToggle);
        SubscribeLocalEvent<CrawlUnderFloorComponent, AttemptClimbEvent>(OnAttemptClimb);
        SubscribeLocalEvent<MapGridComponent, TileChangedEvent>(OnTileChanged);
        SubscribeLocalEvent<CrawlUnderFloorComponent, MoveEvent>(OnMove);
        SubscribeLocalEvent<CrawlUnderFloorComponent, PreventCollideEvent>(OnPreventCollision);
        SubscribeLocalEvent<CrawlUnderFloorComponent, AttackAttemptEvent>(OnAttemptAttack);
    }

    private void OnMapInit(EntityUid uid, CrawlUnderFloorComponent component, MapInitEvent args)
    {
        if (component.ToggleHideAction == null)
            _actionsSystem.AddAction(uid, ref component.ToggleHideAction, component.ActionProto);
        component.WasOnSubfloor = IsOnSubfloor(uid);

        if (!_net.IsClient)
        {
            EnableSneakMode(uid, component);
            SetStealth(uid, !IsOnSubfloor(uid)); // We use stealth component for allowing medhuds and such to be hidden, terrible solution, couldn't think of anything better.
        }
    }

    private void OnAbilityToggle(EntityUid uid, CrawlUnderFloorComponent component, ToggleCrawlingStateEvent args)
    {
        if (args.Handled)
            return;

        if (_net.IsClient)
        {
            args.Handled = true;
            return;
        }

        if (TryComp<VentCrawlerComponent>(uid, out var vent) && vent.InTube)
        {
            args.Handled = true;
            return;
        }

        var wasOnSubfloor = IsOnSubfloor(uid);
        var result = component.Enabled ? DisableSneakMode(uid, component) : EnableSneakMode(uid, component);

        RefreshCrawlSubfloorState(uid, component, false);

        if (component.Enabled)
            SetStealth(uid, !component.WasOnSubfloor);
        else
            SetStealth(uid, false);

        if (!wasOnSubfloor)
            PryTileIfUnder(uid, component);

        var enabling = component.Enabled;
        var selfKey = enabling ? "crawl-under-floor-toggle-on-self" : "crawl-under-floor-toggle-off-self";
        var othersKey = enabling ? "crawl-under-floor-toggle-on" : "crawl-under-floor-toggle-off";

        _popup.PopupEntity(Loc.GetString(selfKey), uid, uid);
        _popup.PopupEntity(Loc.GetString(othersKey, ("name", Name(uid))), uid, Filter.PvsExcept(uid), true, PopupType.Medium);

        args.Handled = result;
    }


    private void OnAttemptClimb(EntityUid uid, CrawlUnderFloorComponent component, AttemptClimbEvent args)
    {
        if (component.Enabled)
            args.Cancelled = true;
    }

    private void OnTileChanged(EntityUid gridUid, MapGridComponent grid, ref TileChangedEvent args)
    {
        // Only check floor goblins that are on the same grid — and only if their tile position
        // matches the changed tile, avoiding a full entity sweep.
        var query = EntityQueryEnumerator<CrawlUnderFloorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (!comp.Enabled)
                continue;

            if (_transform.GetGrid(xform.Coordinates) is not { } g || g != gridUid)
                continue;

            // g == gridUid here, so the MapGridComponent is already `grid` from the event args.
            var entityTile = _map.TileIndicesFor((gridUid, grid), xform.Coordinates);

            // Only process if the entity is standing on one of the changed tiles.
            var relevant = false;
            foreach (var change in args.Changes)
            {
                if (change.GridIndices == entityTile)
                {
                    relevant = true;
                    break;
                }
            }

            if (!relevant)
                continue;

            ProcessCrawlStateChange(uid, comp, true);
        }
    }

    private void OnMove(EntityUid uid, CrawlUnderFloorComponent comp, ref MoveEvent args)
    {
        if (!comp.Enabled)
            return;

        // Use the position from MoveEvent to avoid an extra Transform() lookup.
        var coords = args.NewPosition;
        if (_transform.GetGrid(coords) is not { } gridUid)
            return;
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var currentTile = _map.TileIndicesFor((gridUid, grid), coords);
        if (comp.LastTile == currentTile)
            return;

        comp.LastTile = currentTile;
        ProcessCrawlStateChange(uid, comp, false);
    }


    private void OnAttemptAttack(EntityUid uid, CrawlUnderFloorComponent comp, AttackAttemptEvent args)
    {
        if (comp.Enabled)
            args.Cancel();
    }

    private void OnPreventCollision(EntityUid uid, CrawlUnderFloorComponent component, ref PreventCollideEvent args)
    {
        var otherUid = args.OtherEntity;

        // Always prevent collision with mobs
        if (_mobStateQuery.HasComp(otherUid))
        {
            args.Cancelled = true;
            return;
        }

        if (!component.Enabled)
            return;

        // Handle airlocks - allow phasing in stealth mode
        if (_airlockQuery.HasComp(otherUid))
        {
            args.Cancelled = true;
            return;
        }

        // Handle conveyor belts - allow phasing in stealth mode
        if (_conveyorQuery.HasComp(otherUid))
        {
            args.Cancelled = true;
            return;
        }
    }

    protected void PlayDuendeSound(EntityUid uid, float probability = 0.3f)
    {
        if (_random.Prob(probability))
        {
            _audio.PlayPvs(new SoundCollectionSpecifier("DuendeSounds"), uid);
        }
    }

    protected bool EnableSneakMode(EntityUid uid, CrawlUnderFloorComponent component)
    {
        if (TryComp<VentCrawlerComponent>(uid, out var vent) && vent.InTube)
            return false;
        if (component.Enabled || (TryComp<ClimbingComponent>(uid, out var climbing) && climbing.IsClimbing))
            return false;
        component.Enabled = true;
        Dirty(uid, component);
        return true;
    }

    protected bool DisableSneakMode(EntityUid uid, CrawlUnderFloorComponent component)
    {
        if (TryComp<VentCrawlerComponent>(uid, out var vent) && vent.InTube)
            return false;
        if (!component.Enabled || IsOnCollidingTile(uid) || (TryComp<ClimbingComponent>(uid, out var climbing) && climbing.IsClimbing))
            return false;
        component.Enabled = false;
        Dirty(uid, component);
        if (TryComp(uid, out FixturesComponent? fixtureComponent))
        {
            foreach (var (key, originalMask) in component.ChangedFixtures)
                if (fixtureComponent.Fixtures.TryGetValue(key, out var fixture))
                    _physics.SetCollisionMask(uid, key, fixture, originalMask, fixtureComponent);
            foreach (var (key, originalLayer) in component.ChangedFixtureLayers)
                if (fixtureComponent.Fixtures.TryGetValue(key, out var fixture))
                    _physics.SetCollisionLayer(uid, key, fixture, originalLayer, fixtureComponent);
        }
        component.ChangedFixtures.Clear();
        component.ChangedFixtureLayers.Clear();
        return true;
    }


    public bool IsOnCollidingTile(EntityUid uid)
    {
        // If we're under the floor, don't consider any tiles as colliding
        if (TryComp<CrawlUnderFloorComponent>(uid, out var crawlComp) &&
            crawlComp.Enabled &&
            !IsOnSubfloor(uid))
        {
            return false;
        }

        // Standard collision check for tiles
        if (!TryGetCurrentTile(uid, out var tileRef, out _) || tileRef.Tile.IsEmpty)
            return false;

        return _turf.IsTileBlocked(tileRef, CollisionGroup.MobMask);
    }

    public bool IsOnSubfloor(EntityUid uid)
    {
        if (!TryGetCurrentTile(uid, out var tileRef, out _))
            return false;
        if (tileRef.Tile.IsEmpty)
            return false;
        var tileDef = (ContentTileDefinition) _tileManager[tileRef.Tile.TypeId];
        return tileDef.IsSubFloor;
    }

    private bool IsInSpace(EntityUid uid)
    {
        if (!TryGetCurrentTile(uid, out var tileRef, out _))
            return true;
        return tileRef.Tile.IsEmpty;
    }

    public bool IsHidden(EntityUid uid, CrawlUnderFloorComponent comp)
        => comp.Enabled; // No longer check for subfloor, just check if crawling is enabled

    private void HandleCrawlTransition(EntityUid uid, bool wasOnSubfloor, bool isOnSubfloor, CrawlUnderFloorComponent comp, bool causedByTileChange)
    {
        if (!_net.IsServer)
            return;
        if (!comp.Enabled)
            return;
        if (wasOnSubfloor == isOnSubfloor)
            return;

        var movedOutOfCover = !wasOnSubfloor && isOnSubfloor;
        var enteredCover = wasOnSubfloor && !isOnSubfloor;

        if (enteredCover)
            SetStealth(uid, true);
        else if (movedOutOfCover)
            SetStealth(uid, false);

        if (movedOutOfCover)
            PlayDuendeSound(uid, causedByTileChange ? 1f : 0.3f);
    }

    private void PryTileIfUnder(EntityUid uid, CrawlUnderFloorComponent comp)
    {
        if (!TryGetCurrentTile(uid, out var tileRef, out var snapPos, out var gridUid))
            return;
        if (tileRef.Tile.IsEmpty || ((ContentTileDefinition) _tileManager[tileRef.Tile.TypeId]).IsSubFloor)
            return;

        _audio.PlayPvs(comp.PrySound, uid);
        _tile.PryTile(snapPos, gridUid);
    }

    private void UpdateCollisionMask(EntityUid uid, CrawlUnderFloorComponent component, bool stealthMode)
    {
        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return;

        if (stealthMode)
        {
            // Save originals before overwriting, so DisableSneakMode can restore them.
            foreach (var (id, fixture) in fixtures.Fixtures)
            {
                // Only save if we haven't already saved this fixture.
                if (component.ChangedFixtures.FindIndex(t => t.key == id) < 0)
                    component.ChangedFixtures.Add((id, fixture.CollisionMask));
                if (component.ChangedFixtureLayers.FindIndex(t => t.key == id) < 0)
                    component.ChangedFixtureLayers.Add((id, fixture.CollisionLayer));

                _physics.SetCollisionMask(uid, id, fixture, (int) CollisionGroup.SmallMobMask, fixtures);
                _physics.SetCollisionLayer(uid, id, fixture, (int) CollisionGroup.SmallMobLayer, fixtures);
            }
        }
        else
        {
            // Restore from saved originals instead of hard-coding MobMask/MobLayer.
            foreach (var (key, originalMask) in component.ChangedFixtures)
            {
                if (fixtures.Fixtures.TryGetValue(key, out var fixture))
                    _physics.SetCollisionMask(uid, key, fixture, originalMask, fixtures);
            }

            foreach (var (key, originalLayer) in component.ChangedFixtureLayers)
            {
                if (fixtures.Fixtures.TryGetValue(key, out var fixture))
                    _physics.SetCollisionLayer(uid, key, fixture, originalLayer, fixtures);
            }

            component.ChangedFixtures.Clear();
            component.ChangedFixtureLayers.Clear();
        }
    }

    private void SetStealth(EntityUid uid, bool enabled)
    {
        if (!TryComp<CrawlUnderFloorComponent>(uid, out var comp))
            return;

        // Update collision mask based on stealth state
        UpdateCollisionMask(uid, comp, enabled);

        // Evil hud overlay hiding shitcode that hijacks StealthComponent
        if (enabled)
        {
            var stealth = EnsureComp<StealthComponent>(uid);
            if (!stealth.Enabled)
            {
                _stealth.SetEnabled(uid, true);
                Dirty(uid, stealth);
            }
        }
        else
        {
            if (TryComp<StealthComponent>(uid, out var stealth) && stealth.Enabled)
            {
                _stealth.SetEnabled(uid, false);
                Dirty(uid, stealth);
            }
        }
    }

    private void RefreshCrawlSubfloorState(EntityUid uid, CrawlUnderFloorComponent comp, bool causedByTileChange)
    {
        var now = IsOnSubfloor(uid);
        var old = comp.WasOnSubfloor;
        comp.WasOnSubfloor = now;

        HandleCrawlTransition(uid, old, now, comp, causedByTileChange);
    }

    private void ProcessCrawlStateChange(EntityUid uid, CrawlUnderFloorComponent comp, bool causedByTileChange)
    {
        if (comp.Enabled && IsInSpace(uid))
        {
            DisableSneakMode(uid, comp);
            return;
        }

        RefreshCrawlSubfloorState(uid, comp, causedByTileChange);
    }

    private bool TryGetCurrentTile(EntityUid uid, out TileRef tileRef, out Vector2i snapPos)
        => TryGetCurrentTile(uid, out tileRef, out snapPos, out _);

    private bool TryGetCurrentTile(EntityUid uid, out TileRef tileRef, out Vector2i snapPos, out EntityUid gridUid)
    {
        var transform = Transform(uid);
        tileRef = default;
        snapPos = default;
        gridUid = default;
        if (_transform.GetGrid(transform.Coordinates) is not { } gid)
            return false;
        if (!TryComp<MapGridComponent>(gid, out var grid))
            return false;
        gridUid = gid;
        snapPos = _map.TileIndicesFor((gid, grid), transform.Coordinates);
        tileRef = _map.GetTileRef(gid, grid, snapPos);
        return true;
    }
}
