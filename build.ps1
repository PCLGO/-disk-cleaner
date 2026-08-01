[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$packageVersion = '1.0.3'
$packageRoot = Join-Path $repoRoot ".packages\Microsoft.NETFramework.ReferenceAssemblies.net48.$packageVersion"
$referenceAssembly = Join-Path $packageRoot 'build\.NETFramework\v4.8\mscorlib.dll'

if (-not (Test-Path -LiteralPath $referenceAssembly)) {
    $packagesDirectory = Join-Path $repoRoot '.packages'
    $archive = Join-Path $packagesDirectory "Microsoft.NETFramework.ReferenceAssemblies.net48.$packageVersion.zip"
    New-Item -ItemType Directory -Path $packagesDirectory -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri "https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/$packageVersion" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $packageRoot -Force
    Remove-Item -LiteralPath $archive -Force
}

$msbuildCandidates = @(
    'D:\VS2022BT\MSBuild\Current\Bin\MSBuild.exe',
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $msbuild) {
    $where = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $where) {
        $install = & $where -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($install) { $msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe' }
    }
}
if (-not $msbuild -or -not (Test-Path -LiteralPath $msbuild)) {
    throw 'MSBuild was not found. Install the .NET desktop workload for Visual Studio 2022 Build Tools.'
}

& $msbuild (Join-Path $repoRoot 'DiskCleanupAssistant.sln') /t:Rebuild "/p:Configuration=$Configuration" /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

if (-not $SkipTests) {
    $testExe = Join-Path $repoRoot "app-tests\bin\$Configuration\DiskCleanupAssistant.Tests.exe"
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
}

if ($Configuration -eq 'Release') {
    $artifactDir = Join-Path $repoRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
    $sourceExe = Join-Path $repoRoot 'app\bin\Release\DiskCleanupAssistant.exe'
    $artifactExe = Join-Path $artifactDir 'DiskCleanupAssistant.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $artifactExe -Force
    $hash = (Get-FileHash -LiteralPath $artifactExe -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $artifactDir 'DiskCleanupAssistant.exe.sha256') -Value "$hash  DiskCleanupAssistant.exe" -Encoding ascii
    Write-Host "Artifact: $artifactExe"
    Write-Host "SHA-256: $hash"
}
