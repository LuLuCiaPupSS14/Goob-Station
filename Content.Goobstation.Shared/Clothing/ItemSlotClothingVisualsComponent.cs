// SPDX-FileCopyrightText: 2025 Goob-Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.Clothing;

/// <summary>
///     When placed in an item slot, overrides the clothing visuals (worn layers on the wearer)
///     based on which entity prototype is currently in the specified item slot.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ItemSlotClothingVisualsComponent : Component
{
    /// <summary>
    ///     The ID of the ItemSlot to check.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string SlotId = "item";

    /// <summary>
    ///     Maps entity prototype ID → (inventory slot name → list of layer overrides).
    ///     When the item in <see cref="SlotId"/> matches a key here, those layers replace
    ///     the default clothing visuals for the matching inventory slot.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Dictionary<EntProtoId, Dictionary<string, List<PrototypeLayerData>>> VisualsOverride = new();
}
