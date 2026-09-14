# Codex hook: metadata only. Never emits instructions or transcript content.
$ErrorActionPreference = 'Stop'
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $session = [string]$payload.session_id
    $model = [string]$payload.model
    $id = [Guid]::Empty
    if (-not [Guid]::TryParseExact($session, 'D', [ref]$id)) { exit 0 }
    $directory = Join-Path $env:LOCALAPPDATA 'FlowLocal\CodingSignals'
    $path = Join-Path $directory ($session.Substring(0, 29) + '.json')
    if ($payload.hook_event_name -eq 'SessionEnd') {
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
        exit 0
    }
    if ($model.Length -gt 512 -or $model -cnotmatch '^[A-Za-z0-9._/:@+\[\]-]+$') { exit 0 }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $json = @{ sessionId = $session; model = $model; timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($path, $json)
} catch {
    # Detection falls back to unknown; a metadata failure must never block Codex.
}
exit 0
