// SPDX-FileCopyrightText: 2025 Evaisa <mail@evaisa.dev>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.FloorGoblin;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CrawlUnderFloorComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? ToggleHideAction;

    [DataField, AutoNetworkedField]
    public EntProtoId? ActionProto;

    [DataField, AutoNetworkedField]
    public bool Enabled = false;

    [DataField, AutoNetworkedField]
    public List<(string key, int originalMask)> ChangedFixtures = new();

    [DataField, AutoNetworkedField]
    public List<(string key, int originalLayer)> ChangedFixtureLayers = new();

    [DataField]
    public int? OriginalDrawDepth;

    [DataField, AutoNetworkedField]
    public bool WasOnSubfloor;

    /// <summary>
    /// Cached tile index — used to avoid repeated tile lookups on every MoveEvent.
    /// Only reprocesses crawl state when the entity crosses a tile boundary.
    /// </summary>
    [ViewVariables]
    public Vector2i LastTile;

    /// <summary>
    /// What sound to play when opening floor panels
    /// </summary>
    [DataField]
    public SoundSpecifier PrySound = new SoundPathSpecifier("/Audio/Items/crowbar.ogg");
}
