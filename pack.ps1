param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\win-x64'
$installer = Join-Path $root 'installer\FlowLocal.iss'
$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = $isccCommand.Source
if (-not $iscc) {
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
if (-not $iscc -or -not (Test-Path $iscc)) {
    $perUser = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    if (Test-Path $perUser) { $iscc = $perUser }
}
if (-not $PortableOnly -and -not (Test-Path $iscc)) {
    throw 'Inno Setup 6 is required to build the installer (ISCC.exe was not found). Use -PortableOnly for the portable artifact.'
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\FlowLocal.App\FlowLocal.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false `
    -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if ($PortableOnly) {
    Write-Host "Portable artifact published to $publish; installer build skipped."
    return
}

& $iscc "/DAppVersion=$Version" $installer
if ($LASTEXITCODE) { throw "ISCC failed with exit code $LASTEXITCODE." }

