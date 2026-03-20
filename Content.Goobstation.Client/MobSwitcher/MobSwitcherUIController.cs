// SPDX-FileCopyrightText: 2026 Goobstation
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Gameplay;
using Content.Goobstation.Shared.MobSwitcher;
using Content.Shared.Input;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Input.Binding;

namespace Content.Goobstation.Client.MobSwitcher;

[UsedImplicitly]
public sealed class MobSwitcherUIController : UIController, IOnStateChanged<GameplayState>, IOnSystemChanged<MobSwitcherSystem>
{
    [Dependency] private readonly IEntityNetworkManager _net = default!;

    [UISystemDependency] private readonly MobSwitcherSystem? _system = default;

    private MobSwitcherWindow? _window;

    // ── System lifecycle ──────────────────────────────────────────────────────

    public void OnSystemLoaded(MobSwitcherSystem system)
    {
        system.StateUpdated += OnStateUpdate;
    }

    public void OnSystemUnloaded(MobSwitcherSystem system)
    {
        system.StateUpdated -= OnStateUpdate;
    }

    // ── State lifecycle ───────────────────────────────────────────────────────

    public void OnStateEntered(GameplayState state)
    {
        CommandBinds.Builder
            .Bind(ContentKeyFunctions.OpenMobSwitcher,
                InputCmdHandler.FromDelegate(_ => ToggleWindow()))
            .Register<MobSwitcherUIController>();
    }

    public void OnStateExited(GameplayState state)
    {
        CommandBinds.Unregister<MobSwitcherUIController>();
        CloseWindow();
    }

    // ── Network events ────────────────────────────────────────────────────────

    private void OnStateUpdate(MobSwitcherStateEvent ev)
    {
        EnsureWindow();
        _window!.Populate(ev.Entries);
    }

    // ── Window management ─────────────────────────────────────────────────────

    private void ToggleWindow()
    {
        if (_window is { IsOpen: true })
        {
            _window.Close();
        }
        else
        {
            EnsureWindow();
            _net.SendSystemNetworkMessage(new MobSwitcherRequestStateEvent());
            _window!.OpenCentered();
        }
    }

    private void EnsureWindow()
    {
        if (_window is { Disposed: false })
            return;

        _window = UIManager.CreateWindow<MobSwitcherWindow>();

        _window.AddCurrentPressed += () =>
            _net.SendSystemNetworkMessage(new MobSwitcherAddCurrentEvent());

        _window.SwitchPressed += target =>
            _net.SendSystemNetworkMessage(new MobSwitcherSwitchEvent { Target = target });

        _window.RemovePressed += target =>
            _net.SendSystemNetworkMessage(new MobSwitcherRemoveEvent { Target = target });

        _window.ResetPressed += () =>
            _net.SendSystemNetworkMessage(new MobSwitcherResetEvent());
    }

    private void CloseWindow()
    {
        _window?.Close();
        _window = null;
    }
}
