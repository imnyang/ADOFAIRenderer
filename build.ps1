param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice',
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

$ffmpeg = Get-ChildItem packages/ffmpeg -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($FetchFFmpeg -and !$ffmpeg) {
    New-Item -ItemType Directory -Force packages/ffmpeg | Out-Null
    $archive = Join-Path $PSScriptRoot 'packages/ffmpeg/essentials.zip'
    # Windows build linked by ffmpeg.org/download.html; keep its original package
    # (including licenses/source links) under packages rather than replacing game FFmpeg.
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath packages/ffmpeg -Force
    Remove-Item -LiteralPath $archive
    $ffmpeg = Get-ChildItem packages/ffmpeg -Filter ffmpeg.exe -Recurse | Select-Object -First 1
}
if ($ffmpeg) {
    Copy-Item -LiteralPath $ffmpeg.FullName -Destination ADOFAIRenderer/bin/Release/ffmpeg.exe -Force
    $ffmpegRoot = Split-Path (Split-Path $ffmpeg.FullName -Parent) -Parent
    $ffmpegLicense = Join-Path $ffmpegRoot 'LICENSE'
    $ffmpegReadme = Join-Path $ffmpegRoot 'README.txt'
    if (Test-Path -LiteralPath $ffmpegLicense) {
        Copy-Item -LiteralPath $ffmpegLicense -Destination ADOFAIRenderer/bin/Release/FFmpeg-LICENSE.txt -Force
    }
    if (Test-Path -LiteralPath $ffmpegReadme) {
        Copy-Item -LiteralPath $ffmpegReadme -Destination ADOFAIRenderer/bin/Release/FFmpeg-README.txt -Force
    }
}
if ($Test) {
    if (!$ffmpeg) { throw 'Run with -FetchFFmpeg -Test to install an encoding-capable FFmpeg.' }
    & $MSBuildPath Tests/RendererTests.csproj /t:Rebuild /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $testOutput = Join-Path $env:TEMP ('adofai-render-tests-' + [guid]::NewGuid().ToString('N'))
    & ./Tests/bin/Release/RendererTests.exe $ffmpeg.FullName $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Renderer tests failed.' }
    Write-Host "Test videos: $testOutput"
}
Write-Host "Mod output: $PSScriptRoot/ADOFAIRenderer/bin/Release"
