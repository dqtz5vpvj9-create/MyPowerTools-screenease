# ScreenEase Native Desktop UI

## Framework Choice

The desktop client uses WPF on .NET 8:

- Native Windows windowing, controls, input, and rendering.
- Visual Studio solution support with no extra app SDK workload requirement.
- No WebView, browser runtime, Electron shell, or HTML UI layer.
- Small dependency surface; the UI project uses the .NET SDK and WPF only.

WinUI 3 is a strong future option for packaged releases, but WPF is the fastest stable path for this milestone because it builds from the standard Windows desktop SDK and keeps the client lightweight.

## Project

```text
src/ScreenEase.Desktop
```

Key files:

- `MainWindow.xaml`: native UI layout and styles.
- `MainViewModel.cs`: state, commands, and refresh logic.
- `ScreenEaseClient.cs`: dual named-pipe/HTTP client for `ScreenEase.CoreService`.
- `Models.cs`: DTOs matching the public REST API.

## Architecture

The desktop client is a separate frontend. It communicates with the core service through a Windows named pipe by default:

```text
ScreenEase.Desktop -> \\.\pipe\screenease.core -> ScreenEase.CoreService -> ScreenEase.Core
```

The UI can still connect to `http://127.0.0.1:5128` when an HTTP service endpoint is useful for debugging. The UI does not reference `ScreenEase.Core` directly, so the core service remains independently hostable.

## Current UI Coverage

- Service endpoint, connect, refresh, save settings.
- Profile list and profile apply.
- Manual color temperature and brightness controls.
- Enabled, night values, and schedule switches.
- Rest timer start, pause, resume, reset.
- Monitor list from service state.
- Error and last update status.

## Run

Start the core service:

```powershell
$env:ScreenEase__Driver = 'memory'
$env:ScreenEase__SettingsPath = "$PWD\.local\settings.json"
dotnet run --project .\src\ScreenEase.CoreService\ScreenEase.CoreService.csproj -c Release -- --pipe-only
```

Start the native UI:

```powershell
dotnet run --project .\src\ScreenEase.Desktop\ScreenEase.Desktop.csproj -c Release
```

Switch `ScreenEase__Driver` to `windows` when you want the real display driver.
