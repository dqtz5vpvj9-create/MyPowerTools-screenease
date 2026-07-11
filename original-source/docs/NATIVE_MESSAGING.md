# Native Messaging Host

ScreenEase includes a Chromium native messaging host for browser-extension display workflows.

Host name:

```text
com.screenease.native
```

## Publish

```powershell
dotnet publish .\src\ScreenEase.NativeHost\ScreenEase.NativeHost.csproj -c Release -r win-x64 --self-contained false
```

## Install For Current User

```powershell
.\tools\install-native-host.ps1 `
  -HostPath .\src\ScreenEase.NativeHost\bin\Release\net8.0\win-x64\publish\ScreenEase.NativeHost.exe `
  -Browser Both
```

The script writes a manifest to `%LOCALAPPDATA%\ScreenEase` and registers it under HKCU for Chrome and Edge.

The manifest template is shipped beside the host project at:

```text
src/ScreenEase.NativeHost/com.screenease.native.json
```

## Request Format

Messages use the Chromium native messaging framing:

```text
4-byte little-endian JSON byte length
UTF-8 JSON payload
```

Supported commands:

- `ping`
- `state`
- `settings`
- `apply`
- `disable`
- `overlay`
- `toggle_overlay`
- `rest_timer_start`
- `rest_timer_pause`
- `rest_timer_resume`
- `rest_timer_reset`
- `import_settings`

Example payload:

```json
{
  "command": "overlay",
  "enabled": true,
  "opacity": 40,
  "color": "#000000"
}
```

Response shape:

```json
{
  "ok": true,
  "command": "overlay",
  "data": {}
}
```

For safe testing, start the host with:

```powershell
$env:ScreenEase__Driver = 'memory'
```


