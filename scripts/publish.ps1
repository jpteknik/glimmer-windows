# Builds Glimmer for Windows into dist\, each file self-contained (no .NET runtime needed on the computer that runs it):
#   Glimmer-<version>-<rid>.exe        the app
#   Glimmer-<version>-<rid>.com        its console companion (cmd and PowerShell find a .com before an .exe, so
#                                      "Glimmer --export a.jpg b.png" waits, prints and returns the exit status)
#   Glimmer-<version>-<rid>-setup.exe  the installer: both for the current user, Start menu shortcut, "glimmer" on PATH,
#                                      the file types and an uninstaller (src\Glimmer.Setup)
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1            # x64
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Rid win-arm64
param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $env:TEMP "glimmer-build\publish"
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$single = @("-c", $Configuration, "-r", $Rid, "--self-contained", "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true")
$folder = $Configuration.ToLower() + "_" + $Rid

function Publish([string]$project, [string]$into, [string[]]$extra = @()) {
    dotnet publish (Join-Path $root $project) @single @extra --artifacts-path $into
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

Publish "src\Glimmer.App\Glimmer.App.csproj" $artifacts
Publish "src\Glimmer.Console\Glimmer.Console.csproj" $artifacts
# The installer, twice: without a payload it is the uninstaller that goes into the install folder; with one it installs.
Publish "src\Glimmer.Setup\Glimmer.Setup.csproj" (Join-Path $artifacts "uninstaller")
$payload = Join-Path $artifacts ("payload-" + $Rid)
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Copy-Item (Join-Path $artifacts "publish\Glimmer.App\$folder\Glimmer.exe") (Join-Path $payload "Glimmer.exe") -Force
Copy-Item (Join-Path $artifacts "publish\Glimmer.Console\$folder\Glimmer.Console.exe") (Join-Path $payload "Glimmer.com") -Force
Copy-Item (Join-Path $artifacts "uninstaller\publish\Glimmer.Setup\$folder\Glimmer.Setup.exe") (Join-Path $payload "Uninstall.exe") -Force
Publish "src\Glimmer.Setup\Glimmer.Setup.csproj" (Join-Path $artifacts "installer") @("-p:GlimmerPayloadDir=$payload")

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$name = "Glimmer-" + $version + "-" + $Rid
Copy-Item (Join-Path $payload "Glimmer.exe") (Join-Path $dist "$name.exe") -Force
Copy-Item (Join-Path $payload "Glimmer.com") (Join-Path $dist "$name.com") -Force
Copy-Item (Join-Path $artifacts "installer\publish\Glimmer.Setup\$folder\Glimmer.Setup.exe") (Join-Path $dist "$name-setup.exe") -Force
Write-Host "Published $dist\$name.exe, .com and -setup.exe"
