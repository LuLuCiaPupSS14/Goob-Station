// SPDX-FileCopyrightText: 2026 Goobstation
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.MobSwitcher;

namespace Content.Goobstation.Client.MobSwitcher;

/// <summary>
/// Client-side relay that receives <see cref="MobSwitcherStateEvent"/> from the server
/// and exposes a plain C# event so UIControllers can subscribe safely.
/// Subscriptions in Initialize() are never locked by the event bus.
/// </summary>
public sealed class MobSwitcherSystem : EntitySystem
{
    public event Action<MobSwitcherStateEvent>? StateUpdated;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<MobSwitcherStateEvent>(ev => StateUpdated?.Invoke(ev));
    }
}
