# ScreenEase Named Pipe IPC

## Endpoint

Default pipe name:

```text
screenease.core
```

Windows path:

```text
\\.\pipe\screenease.core
```

Desktop endpoint string:

```text
pipe:screenease.core
```

## Run Without TCP

```powershell
$env:ScreenEase__Driver = 'memory'
$env:ScreenEase__SettingsPath = "$PWD\.local\settings.json"
dotnet run --project .\src\ScreenEase.CoreService\ScreenEase.CoreService.csproj -c Release -- --pipe-only
```

`--pipe-only` starts the core loop and named pipe server through the .NET Generic Host. It does not start Kestrel and does not bind an HTTP port.

## Protocol

The pipe uses the same clean JSON command protocol as the Chromium native messaging host:

1. Four-byte little-endian payload length.
2. UTF-8 JSON payload.
3. Four-byte little-endian response length.
4. UTF-8 JSON response.

Maximum message size is 1 MiB.

## Response Envelope

```json
{
  "ok": true,
  "command": "state",
  "data": {},
  "error": null
}
```

## Commands

- `ping`
- `state`
- `settings`
- `update_settings`
- `apply`
- `disable`
- `overlay`
- `toggle_overlay`
- `rest_timer_start`
- `rest_timer_pause`
- `rest_timer_resume`
- `rest_timer_reset`
- `import_settings`

Example apply payload:

```json
{
  "command": "apply",
  "profileId": "long-read",
  "enabled": true
}
```

Example overlay payload:

```json
{
  "command": "overlay",
  "enabled": true,
  "opacity": 40,
  "color": "#000000"
}
```

## Configuration

Change the pipe name:

```powershell
$env:ScreenEase__NamedPipe__Name = 'screenease.core.dev'
```

Disable named pipe hosting in HTTP mode:

```powershell
$env:ScreenEase__NamedPipe__Enabled = 'false'
```
