# Session-only settings; does not overwrite your Claude configuration.
$ErrorActionPreference = 'Stop'
$previousTitle = [Console]::Title
$settings = [IO.Path]::GetTempFileName()
$command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'flowlocal-statusline.ps1') + '"'
try {
    @{ statusLine = @{ type = 'command'; command = $command; refreshInterval = 30 } } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settings -Encoding UTF8
    & claude --settings $settings @args
    $exitCode = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $settings -ErrorAction SilentlyContinue
    [Console]::Title = $previousTitle
}
exit $exitCode
