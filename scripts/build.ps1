param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path (Split-Path -Parent $projectDirectory) '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectDirectory
try {
    & $dotnetCommand restore ScreenTimeCompanion.slnx --configfile NuGet.config -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectFallbackFolders=
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    & $dotnetCommand build ScreenTimeCompanion.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnetCommand test ScreenTimeCompanion.slnx -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & "$projectDirectory\src\ScreenTimeCompanion.Windows\Verify-InputTicks.ps1"
    if ($Publish) {
        & $dotnetCommand publish src/ScreenTimeCompanion.App/ScreenTimeCompanion.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:RestoreConfigFile="$projectDirectory\NuGet.config" -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectFallbackFolders= -o artifacts/ScreenTimeCompanion-win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath "$projectDirectory\docs\08-running-mvp.md" -Destination "$projectDirectory\artifacts\ScreenTimeCompanion-win-x64\使用说明.md" -Force
        $archive = "$projectDirectory\artifacts\ScreenTimeCompanion-v0.1.0-win-x64.zip"
        Compress-Archive -LiteralPath "$projectDirectory\artifacts\ScreenTimeCompanion-win-x64" -DestinationPath $archive -Force
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -LiteralPath ($archive + '.sha256') -Encoding utf8
        Write-Output "Portable Windows release: $archive"
    }
} finally { Pop-Location }
