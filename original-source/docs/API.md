# ScreenEase Core Service API

Default development URL:

```text
http://127.0.0.1:5128
```

All request and response bodies use JSON.

## Health

```http
GET /healthz
```

Returns:

```json
{ "status": "ok" }
```

## State

```http
GET /api/state
```

Returns current settings, applied effect, rest timer, and monitors.

## Settings

```http
GET /api/settings
PUT /api/settings
```

`PUT /api/settings` accepts the full settings object returned by `GET /api/settings`.

Important fields:

- `enabled`: enables display filtering.
- `activeProfileId`: one of `day-office`, `long-read`, `detail-work`, `warm-video`, `bright-focus`, `low-blue-evening`, `personal`, `manual-adjustment`.
- `useSchedule`: switches to night values during the night window.
- `sunrise`, `sunset`: local `HH:mm:ss` times.
- `profiles`: editable profile list.
- `restTimer`: work and break timer settings.

## Profiles

```http
GET /api/profiles
```

Returns the profile list.

## Apply A Profile Or Manual Values

```http
POST /api/apply
```

Body:

```json
{
  "profileId": "long-read",
  "colorTemperatureKelvin": 5500,
  "brightnessPercent": 85,
  "enabled": true
}
```

All properties are optional. Missing color and brightness values are taken from the selected profile.

When manual color or brightness values are applied without an explicit profile, the service creates or updates the `manual-adjustment` profile named `自定义调节` and makes it active.

## Disable Filtering

```http
POST /api/disable
```

Disables filtering and resets display gamma through the active driver.

## Monitors

```http
GET /api/monitors
```

Returns monitor rectangles and primary monitor flag.

## Overlay Dimming

```http
GET /api/overlay
PUT /api/overlay
```

`PUT /api/overlay` body:

```json
{
  "enabled": true,
  "opacityPercent": 40,
  "colorHex": "#000000"
}
```

The Windows driver creates topmost, click-through layered windows over each monitor. The `memory` driver records state for safe testing.

## Hotkeys

```http
GET /api/hotkeys
PUT /api/hotkeys
```

`GET /api/hotkeys` returns configured bindings and successfully active registrations.

`PUT /api/hotkeys` accepts an array:

```json
[
  {
    "id": "toggle-enabled",
    "action": "ToggleEnabled",
    "gesture": "Ctrl+Alt+F9",
    "enabled": true
  }
]
```

Supported gesture syntax uses `+` separated modifiers and keys, for example `Ctrl+Alt+F9`, `Ctrl+Alt+Up`, `Shift+Alt+R`.

Supported actions:

- `ToggleEnabled`
- `IncreaseBrightness`
- `DecreaseBrightness`
- `IncreaseColorTemperature`
- `DecreaseColorTemperature`
- `ApplyLongReadProfile`
- `ApplyLowBlueEveningProfile`
- `ToggleOverlay`

## Rest Timer

```http
GET  /api/rest-timer
POST /api/rest-timer/start
POST /api/rest-timer/pause
POST /api/rest-timer/resume
POST /api/rest-timer/reset
```

Timer phases:

- `Stopped`
- `Work`
- `ShortBreak`
- `LongBreak`
- `Paused`

## Import Legacy Settings

```http
POST /api/import/legacy-settings
```

Body:

```json
{
  "path": "C:\\Path\\To\\settings.dat"
}
```

The importer reads profile color temperature, brightness, schedule, transition, and rest timer values from the INI-style settings file.


