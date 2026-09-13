# Configure this script as Claude Code's statusLine command.
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$model = if ($payload.model.id) { [string]$payload.model.id } else { "unknown" }
$reasoning = if ($payload.effort.level) { [string]$payload.effort.level } else { "unknown" }
$model = ($model.ToLowerInvariant() -replace '[^a-z0-9.-]', '')
$reasoning = ($reasoning.ToLowerInvariant() -replace '[^a-z0-9.-]', '')
if ([string]::IsNullOrWhiteSpace($model)) { $model = "unknown" }
if ([string]::IsNullOrWhiteSpace($reasoning)) { $reasoning = "unknown" }
$stamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$title = "FlowLocal/1|claude-code|$model|$reasoning|$stamp"
$esc = [char]27
[Console]::Out.Write("$esc]0;$title`aClaude Code: $model / $reasoning")
