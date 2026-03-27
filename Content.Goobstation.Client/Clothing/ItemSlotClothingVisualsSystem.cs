// SPDX-FileCopyrightText: 2025 Goob-Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Goobstation.Shared.Clothing;
using Content.Shared.Clothing;
using Content.Shared.Item;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Client.Clothing;

public sealed class ItemSlotClothingVisualsSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ItemSlotClothingVisualsComponent, GetEquipmentVisualsEvent>(OnGetVisuals);
        SubscribeLocalEvent<ItemSlotClothingVisualsComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<ItemSlotClothingVisualsComponent, EntRemovedFromContainerMessage>(OnRemoved);
    }

    private void OnGetVisuals(EntityUid uid, ItemSlotClothingVisualsComponent comp, GetEquipmentVisualsEvent args)
    {
        // Find the item currently in the specified slot
        if (!_container.TryGetContainer(uid, comp.SlotId, out var container) ||
            container.ContainedEntities.Count == 0)
            return;

        var item = container.ContainedEntities[0];

        // Check if this item's prototype has a visuals override defined
        var protoId = MetaData(item).EntityPrototype?.ID;
        if (protoId == null)
            return;

        if (!comp.VisualsOverride.TryGetValue(new EntProtoId(protoId), out var slotOverrides))
            return;

        if (!slotOverrides.TryGetValue(args.Slot, out var layers))
            return;

        // Replace the layers that will be shown on the wearer
        args.Layers.Clear();
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var key = layer.MapKeys?.FirstOrDefault() ?? $"{args.Slot}-override-{i}";
            args.Layers.Add((key, layer));
        }
    }

    private void OnInserted(EntityUid uid, ItemSlotClothingVisualsComponent comp, EntInsertedIntoContainerMessage args)
        => TriggerWearerRender(uid, comp, args.Container.ID);

    private void OnRemoved(EntityUid uid, ItemSlotClothingVisualsComponent comp, EntRemovedFromContainerMessage args)
        => TriggerWearerRender(uid, comp, args.Container.ID);

    private void TriggerWearerRender(EntityUid uid, ItemSlotClothingVisualsComponent comp, string containerId)
    {
        // Only care about our specific slot changing
        if (containerId != comp.SlotId)
            return;

        // If this belt is being worn by someone, trigger a clothing re-render
        if (_container.TryGetContainingContainer((uid, null, null), out var outer))
            RaiseLocalEvent(outer.Owner, new VisualsChangedEvent(GetNetEntity(uid), outer.ID));
    }
}
