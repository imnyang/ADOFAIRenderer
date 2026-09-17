param(
    [string]$GameDir = 'L:\SteamLibrary\steamapps\common\A Dance of Fire and Ice',
    [string]$MSBuildPath,
    [string]$FfmpegPath = 'ffmpeg',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
Set-Location $PSScriptRoot

# Build dependencies remain local and ignored. No game DLL or FFmpeg binary
# is redistributed; FFmpeg is installed by the mod after first-launch consent.
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
& $MSBuildPath OrbitRender.sln /t:Rebuild /p:Configuration=Release "/p:GameDir=$GameDir" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }

# Remove FFmpeg artifacts produced by older versions of this build script.
# The exact Release/FFmpeg directory is generated output, not user data.
$releaseDirectory = Join-Path $PSScriptRoot 'OrbitRender/bin/Release'
$oldFfmpegDirectory = Join-Path $releaseDirectory 'FFmpeg'
if (Test-Path -LiteralPath $oldFfmpegDirectory -PathType Container) {
    Remove-Item -LiteralPath $oldFfmpegDirectory -Recurse -Force
}
foreach ($staleName in @('ffmpeg.exe', 'ffprobe.exe', 'ffmpeg', 'ffprobe', 'FFmpeg-LICENSE.txt', 'FFmpeg-README.txt')) {
    $stalePath = Join-Path $releaseDirectory $staleName
    if (Test-Path -LiteralPath $stalePath -PathType Leaf) { Remove-Item -LiteralPath $stalePath -Force }
}

if ($Test) {
    & $MSBuildPath Tests/RendererTests.csproj /t:Rebuild /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $testOutput = Join-Path $env:TEMP ('orbit-render-tests-' + [guid]::NewGuid().ToString('N'))
    & ./Tests/bin/Release/RendererTests.exe $FfmpegPath $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Renderer tests failed.' }
    Write-Host "Test videos: $testOutput"
}
Write-Host "Mod output: $PSScriptRoot/OrbitRender/bin/Release"
