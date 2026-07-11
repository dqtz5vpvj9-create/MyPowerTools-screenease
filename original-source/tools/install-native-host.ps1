param(
    [Parameter(Mandatory = $true)]
    [string]$HostPath,

    [string]$Name = 'com.screenease.native',

    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string]$Browser = 'Both'
)

$ErrorActionPreference = 'Stop'

$resolvedHost = (Resolve-Path -LiteralPath $HostPath).Path
$manifestDir = Join-Path $env:LOCALAPPDATA 'ScreenEase'
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
$manifestPath = Join-Path $manifestDir "$Name.json"

$manifest = [ordered]@{
    name = $Name
    description = 'ScreenEase native messaging host'
    path = $resolvedHost
    type = 'stdio'
    allowed_origins = @(
        'chrome-extension://poeonbclpmnoomoaidabfndafbnhjcfa/',
        'chrome-extension://dkadfpmghpplllbldajfhglcihbghbgc/',
        'chrome-extension://pacnollcefnfheghikkhbhodhnplhbdm/',
        'chrome-extension://olmpfahephldkkfhkmlnociobiihgndg/'
    )
}

$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$targets = switch ($Browser) {
    'Chrome' { @('HKCU:\Software\Google\Chrome\NativeMessagingHosts') }
    'Edge' { @('HKCU:\Software\Microsoft\Edge\NativeMessagingHosts') }
    default {
        @(
            'HKCU:\Software\Google\Chrome\NativeMessagingHosts',
            'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts'
        )
    }
}

foreach ($root in $targets) {
    $key = Join-Path $root $Name
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $manifestPath
}

[pscustomobject]@{
    Name = $Name
    HostPath = $resolvedHost
    ManifestPath = $manifestPath
    Browser = $Browser
}


