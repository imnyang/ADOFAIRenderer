param(
    [string]$GameDir = 'L:\SteamLibrary\steamapps\common\A Dance of Fire and Ice',
    [string]$MSBuildPath,
    [switch]$FetchFFmpeg,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot


# Build dependencies remain local and ignored. No game DLL is redistributed.
function Get-Package([string]$Id, [string]$Version, [string]$Destination, [string]$Expected) {
    if (Test-Path -LiteralPath (Join-Path $Destination $Expected)) { return }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $archive = Join-Path $Destination 'package.zip'
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$Id/$Version/$Id.$Version.nupkg" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
    Remove-Item -LiteralPath $archive
    if (!(Test-Path -LiteralPath (Join-Path $Destination $Expected))) { throw "Package $Id is incomplete." }
}
Get-Package 'unitymodmanager' '0.32.4' 'packages/UnityModManager' 'lib/net35/UnityModManager.dll'
Get-Package 'microsoft.netframework.referenceassemblies.net48' '1.0.3' 'packages/net48' 'build/.NETFramework/v4.8/mscorlib.dll'
if (!(Test-Path 'packages/0Harmony.dll')) {
    Get-Package 'lib.harmony' '2.2.2' 'packages/Harmony' 'lib/net48/0Harmony.dll'
    Copy-Item -LiteralPath 'packages/Harmony/lib/net48/0Harmony.dll' -Destination 'packages/0Harmony.dll'
}
if (!$MSBuildPath) {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($command) { $MSBuildPath = $command.Source }
    else {
        $rider = Get-ChildItem "${env:ProgramFiles}/JetBrains" -Filter 'JetBrains Rider *' -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($rider) { $MSBuildPath = Join-Path $rider.FullName 'tools/MSBuild/Current/Bin/MSBuild.exe' }
    }
}
if (!$MSBuildPath -or !(Test-Path -LiteralPath $MSBuildPath)) { throw 'Pass -MSBuildPath with Visual Studio or Rider MSBuild.exe.' }
if (!(Test-Path -LiteralPath "$GameDir/A Dance of Fire and Ice_Data/Managed/Assembly-CSharp.dll")) { throw 'Pass -GameDir with your ADOFAI installation.' }
& $MSBuildPath ADOFAIRenderer.sln /t:Rebuild /p:Configuration=Release "/p:GameDir=$GameDir" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }

$ffmpegPackageRoot = Join-Path $PSScriptRoot 'packages/ffmpeg'
$releaseDirectory = Join-Path $PSScriptRoot 'ADOFAIRenderer/bin/Release'
$ffmpegOutputRoot = Join-Path $releaseDirectory 'FFmpeg'

function Find-FfmpegBinary([string]$Platform) {
    $expectedName = if ($Platform -eq 'windows-x64') { 'ffmpeg.exe' } else { 'ffmpeg' }
    $platformDirectory = Join-Path $ffmpegPackageRoot $Platform
    if (Test-Path -LiteralPath (Join-Path $platformDirectory $expectedName)) {
        return Get-Item -LiteralPath (Join-Path $platformDirectory $expectedName)
    }
    # Do not reuse another Unix platform's extensionless binary. Only the
    # legacy Windows package layout is safe to search recursively.
    if ($Platform -ne 'windows-x64') { return $null }
    if (!(Test-Path -LiteralPath $ffmpegPackageRoot)) { return $null }
    return Get-ChildItem -LiteralPath $ffmpegPackageRoot -Filter $expectedName -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Save-FfmpegMetadata([string]$Platform, [string]$SearchRoot) {
    $platformDirectory = Join-Path $ffmpegPackageRoot $Platform
    New-Item -ItemType Directory -Force -Path $platformDirectory | Out-Null
    $license = Get-ChildItem -LiteralPath $SearchRoot -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(GPLv3|LICENSE)' } | Select-Object -First 1
    $readme = Get-ChildItem -LiteralPath $SearchRoot -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^README|^readme' } | Select-Object -First 1
    if ($license) { Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $platformDirectory 'FFmpeg-LICENSE.txt') -Force }
    if ($readme) { Copy-Item -LiteralPath $readme.FullName -Destination (Join-Path $platformDirectory 'FFmpeg-README.txt') -Force }
}

function Fetch-FfmpegPlatform([string]$Platform) {
    $platformDirectory = Join-Path $ffmpegPackageRoot $Platform
    New-Item -ItemType Directory -Force -Path $platformDirectory | Out-Null
    $downloadDirectory = Join-Path $ffmpegPackageRoot 'downloads'
    $extractDirectory = Join-Path $downloadDirectory $Platform
    New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null

    if ($Platform -eq 'windows-x64') {
        $archive = Join-Path $downloadDirectory 'ffmpeg-windows-x64.zip'
        if (!(Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory -Force
    }
    elseif ($Platform -eq 'linux-x64') {
        $archive = Join-Path $downloadDirectory 'ffmpeg-linux-x64.tar.xz'
        if (!(Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest 'https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz' -OutFile $archive
        }
        New-Item -ItemType Directory -Force -Path $extractDirectory | Out-Null
        & tar.exe -xf $archive -C $extractDirectory | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the Linux FFmpeg archive. Windows tar.exe is required.' }
    }
    elseif ($Platform -eq 'macos-x64') {
        $archive = Join-Path $downloadDirectory 'ffmpeg-macos-x64.zip'
        if (!(Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest 'https://evermeet.cx/ffmpeg/getrelease/zip' -OutFile $archive
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory -Force
    }
    elseif ($Platform -eq 'macos-arm64') {
        $archive = Join-Path $downloadDirectory 'ffmpeg-macos-arm64.zip'
        if (!(Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest 'https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip' -OutFile $archive
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $extractDirectory -Force
    }
    else { throw "Unsupported FFmpeg platform: $Platform" }

    $expectedName = if ($Platform -eq 'windows-x64') { 'ffmpeg.exe' } else { 'ffmpeg' }
    $source = Get-ChildItem -LiteralPath $extractDirectory -Filter $expectedName -File -Recurse |
        Select-Object -First 1
    if (!$source) { throw "The downloaded FFmpeg archive did not contain $expectedName ($Platform)." }
    Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $platformDirectory $expectedName) -Force
    $probeName = if ($Platform -eq 'windows-x64') { 'ffprobe.exe' } else { 'ffprobe' }
    $probe = Get-ChildItem -LiteralPath $extractDirectory -Filter $probeName -File -Recurse |
        Select-Object -First 1
    if ($probe) { Copy-Item -LiteralPath $probe.FullName -Destination (Join-Path $platformDirectory $probeName) -Force }
    Save-FfmpegMetadata $Platform $extractDirectory
    return Get-Item -LiteralPath (Join-Path $platformDirectory $expectedName)
}

function Stage-FfmpegPlatform([string]$Platform, [switch]$CopyToRoot) {
    $source = Find-FfmpegBinary $Platform
    # A normal build is a complete distributable build: every supported
    # platform must be present even when the host is not Windows. Keep the
    # legacy -FetchFFmpeg switch accepted for compatibility, but fetch any
    # missing platform automatically so the output cannot silently be partial.
    if (!$source) { $source = Fetch-FfmpegPlatform $Platform }
    if (!$source) { return $null }

    $expectedName = if ($Platform -eq 'windows-x64') { 'ffmpeg.exe' } else { 'ffmpeg' }
    $destinationDirectory = Join-Path $ffmpegOutputRoot $Platform
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $destination = Join-Path $destinationDirectory $expectedName
    Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
    $probeName = if ($Platform -eq 'windows-x64') { 'ffprobe.exe' } else { 'ffprobe' }
    $probeSource = Join-Path (Split-Path $source.FullName -Parent) $probeName
    if (Test-Path -LiteralPath $probeSource) {
        Copy-Item -LiteralPath $probeSource -Destination (Join-Path $destinationDirectory $probeName) -Force
    }
    if ($CopyToRoot) { Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $releaseDirectory $expectedName) -Force }

    $sourceDirectory = Split-Path $source.FullName -Parent
    foreach ($metadata in @('FFmpeg-LICENSE.txt', 'FFmpeg-README.txt')) {
        $metadataSource = $null
        $sourceNames = if ($metadata -eq 'FFmpeg-LICENSE.txt') {
            @($metadata, 'GPLv3.txt', 'LICENSE')
        } else {
            @($metadata, 'README.txt', 'readme.txt')
        }
        foreach ($metadataDirectory in @($sourceDirectory, (Split-Path $sourceDirectory -Parent), $ffmpegPackageRoot)) {
            foreach ($sourceName in $sourceNames) {
                $candidate = Join-Path $metadataDirectory $sourceName
                if (Test-Path -LiteralPath $candidate) { $metadataSource = $candidate; break }
            }
            if ($metadataSource) { break }
        }
        if ($metadataSource -and (Test-Path -LiteralPath $metadataSource)) {
            Copy-Item -LiteralPath $metadataSource -Destination (Join-Path $destinationDirectory $metadata) -Force
            if (!(Test-Path -LiteralPath (Join-Path $releaseDirectory $metadata))) {
                Copy-Item -LiteralPath $metadataSource -Destination (Join-Path $releaseDirectory $metadata) -Force
            }
        }
    }
    return Get-Item -LiteralPath $destination
}

function Ensure-FfmpegPlatformMetadata([string]$Platform) {
    $destinationDirectory = Join-Path $ffmpegOutputRoot $Platform
    if (!(Test-Path -LiteralPath $destinationDirectory)) { return }
    $rootLicense = Join-Path $releaseDirectory 'FFmpeg-LICENSE.txt'
    $licenseDestination = Join-Path $destinationDirectory 'FFmpeg-LICENSE.txt'
    if (!(Test-Path -LiteralPath $licenseDestination) -and (Test-Path -LiteralPath $rootLicense)) {
        Copy-Item -LiteralPath $rootLicense -Destination $licenseDestination -Force
    }
    $readmeDestination = Join-Path $destinationDirectory 'FFmpeg-README.txt'
    if (!(Test-Path -LiteralPath $readmeDestination)) {
        $source = switch ($Platform) {
            'linux-x64' { 'https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz' }
            'macos-x64' { 'https://evermeet.cx/ffmpeg/getrelease/zip' }
            'macos-arm64' { 'https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip' }
            default { 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' }
        }
        @(
            "FFmpeg platform package: $Platform"
            "Source archive: $source"
            'The FFmpeg binary is a separate GPLv3 component; see FFmpeg-LICENSE.txt.'
        ) | Set-Content -LiteralPath $readmeDestination -Encoding UTF8
    }
}

$windowsFfmpeg = Stage-FfmpegPlatform 'windows-x64' -CopyToRoot
$linuxFfmpeg = Stage-FfmpegPlatform 'linux-x64'
$macosFfmpeg = Stage-FfmpegPlatform 'macos-x64'
$macosArmFfmpeg = Stage-FfmpegPlatform 'macos-arm64'
Ensure-FfmpegPlatformMetadata 'windows-x64'
Ensure-FfmpegPlatformMetadata 'linux-x64'
Ensure-FfmpegPlatformMetadata 'macos-x64'
Ensure-FfmpegPlatformMetadata 'macos-arm64'
$ffmpeg = $windowsFfmpeg
if ($Test) {
    if (!$ffmpeg) { throw 'The complete build did not produce the Windows FFmpeg package.' }
    & $MSBuildPath Tests/RendererTests.csproj /t:Rebuild /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $testOutput = Join-Path $env:TEMP ('adofai-render-tests-' + [guid]::NewGuid().ToString('N'))
    & ./Tests/bin/Release/RendererTests.exe $ffmpeg.FullName $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Renderer tests failed.' }
    Write-Host "Test videos: $testOutput"
}
Write-Host "Mod output: $PSScriptRoot/ADOFAIRenderer/bin/Release"
