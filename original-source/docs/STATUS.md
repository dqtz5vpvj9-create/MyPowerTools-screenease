# Status

## Completed In This Milestone

- MIT-licensed .NET 8 solution with front-end-agnostic core service.
- Windows gamma driver using `SetDeviceGammaRamp`.
- Gamma ramp generation compatible with observed behavior.
- Memory driver for safe API and CI-style checks.
- Public-literature-based profiles, brightness, color temperature, day/night schedule.
- Internal layered-window overlay dimming compatibility path.
- Global hotkey registration path.
- Chromium native messaging stdio host.
- Rest timer state machine.
- JSON settings persistence.
- Legacy INI settings importer.
- Windows named pipe IPC and `--pipe-only` service mode.
- REST API documentation.
- Native WPF/.NET 8 desktop UI.
- Desktop auto-start for the local core service.
- No-package test runner.

## Verified

- `dotnet build .\ScreenEase.sln -c Release`
- `dotnet run --project .\tests\ScreenEase.Tests\ScreenEase.Tests.csproj -c Release`
- 16 no-package tests passed.
- Pipe-only service smoke test with `ping`, `state`, `apply`, and `overlay`.
- Pipe-only smoke confirmed 0 TCP listeners for the service process.
- HTTP smoke test with `ScreenEase__Driver=memory`
- HTTP smoke test for `/api/overlay` and `/api/hotkeys`
- Native messaging codec and command-handler tests.
- Native host process smoke test with length-prefixed `overlay` command.
- Native desktop UI Release build.
- Legacy settings import from an INI-style settings file.

## Remaining Parity Work

- Windows service or tray-host packaging.
- Desktop tray controls and packaging.
- More runtime testing across multi-monitor hardware with the Windows driver enabled.


