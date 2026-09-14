# Configure this script as Claude Code's statusLine command.
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$model = if ($payload.model.id) { [string]$payload.model.id } else { "unknown" }
$reasoning = if ($payload.effort.level) { [string]$payload.effort.level } else { "unknown" }
if ($model.Length -gt 512 -or $model -cnotmatch '^[A-Za-z0-9._/:@+\[\]-]+$') { $model = "unknown" }
if ($reasoning.Length -gt 512 -or $reasoning -cnotmatch '^[A-Za-z0-9._/:@+\[\]-]+$') { $reasoning = "unknown" }
$stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$title = "FlowLocal/1|claude-code|$model|$reasoning|$stamp"
# Set the console title directly: Claude captures status-line stdout.
try { [Console]::Title = $title } catch { }
$esc = [char]27
[Console]::Out.Write("$esc]0;$title`aClaude Code: $model / $reasoning")
