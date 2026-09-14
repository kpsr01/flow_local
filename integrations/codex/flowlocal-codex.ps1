# Session-only config. Review/enable these metadata hooks in Codex's /hooks UI.
$ErrorActionPreference = 'Stop'
$previousTitle = [Console]::Title
$script = "& '" + (Join-Path $PSScriptRoot 'flowlocal-signal.ps1').Replace("'", "''") + "'"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
# Avoid nested native quoting on Windows PowerShell 5.1.
$command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
$hook = "[{hooks=[{type='command',command='$command',timeout=3}]}]"
$config = @('-c', "tui.terminal_title=['app-name','session-id','model','reasoning']")
foreach ($event in @('SessionStart', 'UserPromptSubmit', 'Stop', 'SessionEnd')) {
    $config += @('-c', "hooks.$event=$hook")
}
try { & codex @config @args; $exitCode = $LASTEXITCODE }
finally { [Console]::Title = $previousTitle }
exit $exitCode
