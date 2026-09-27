# Cartoon Terrain Studio: paint, generate and explore procedural 2.5D cartoon terrain.
#
#   Start.cmd                      open the studio (in the mode used last; the welcome card on the first start)
#   Start.cmd -Mode generate       start in a mode: paint | generate | world
#   Start.cmd -Seed amber-vale-7   start Explore with this seed
#   Start.cmd -Map C:\my.terrain.json   open a saved map in Paint
#   Start.cmd -Quality ultra       graphics quality: auto, low, medium, high, ultra (default: the setting in the studio)
#   Start.cmd -Editor              open the project in the Godot editor
#   Start.cmd -PrepareOnly         only download the engine, build and import
#   Start.cmd -NoBuild             start the last built state without the C# build
#
# The first start downloads Godot 4.7.2 .NET into .tools, builds the C# project and imports the shaders (about a
# minute). Requirement: .NET SDK 8. The project also opens directly in any Godot 4.7 .NET editor (see README.md).
param(
    [ValidateSet('', 'paint', 'generate', 'world')]
    [string]$Mode = '',
    [string]$Seed = '',
    [string]$Map = '',
    [ValidateSet('', 'auto', 'low', 'medium', 'high', 'ultra')]
    [string]$Quality = '',
    [ValidateSet('auto', 'd3d12', 'vulkan')]
    [string]$RenderingDriver = 'auto',
    [switch]$Editor,
    [switch]$PrepareOnly,
    [switch]$NoBuild,
    [string[]]$GodotArguments = @()
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

# -- Engine: Godot 4.7.2 .NET (downloaded to .tools when needed) --
$engineName = 'Godot_v4.7.2-stable_mono_win64'
$tools = Join-Path $PSScriptRoot '.tools'
$engineDir = Join-Path $tools $engineName
$engine = Join-Path $engineDir "$engineName.exe"
$console = Join-Path $engineDir "${engineName}_console.exe"
if (-not (Test-Path -LiteralPath $engine)) {
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $archive = Join-Path $tools "$engineName.zip"
    Write-Host "Downloading $engineName ..."
    Invoke-WebRequest -Uri "https://github.com/godotengine/godot-builds/releases/download/4.7.2-stable/$engineName.zip" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force
    Remove-Item -LiteralPath $archive -Force
}
if (-not (Test-Path -LiteralPath $engine)) { throw "Godot 4.7.2 .NET is missing under $engineDir." }

# -- C# build (Godot loads the Debug configuration, which this project builds optimised) --
if (-not $NoBuild) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    if (-not (Test-Path -LiteralPath $dotnet)) { throw '.NET SDK 8 is missing (https://dotnet.microsoft.com/download).' }
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'   # build messages in English on any system language
    & $dotnet build (Join-Path $PSScriptRoot 'CartoonTerrainStudio.csproj') -c Debug -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The C# build failed.' }
}

# -- First start: Godot imports shaders and textures once --
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.godot\imported'))) {
    Write-Host 'First start: Godot is importing the project ...'
    & $console --headless --path $PSScriptRoot --import | Out-Host
}
if ($PrepareOnly) { return }

$arguments = @('--path', $PSScriptRoot) + $GodotArguments
if ($RenderingDriver -ne 'auto') { $arguments += @('--rendering-driver', $RenderingDriver) }
if ($Editor) {
    $arguments += '--editor'
} else {
    $arguments += '--'
    if ($Mode) { $arguments += @('--mode', $Mode) }
    if ($Quality) { $arguments += @('--quality', $Quality) }
    if ($Seed) { $arguments += @('--seed', $Seed) }
    if ($Map) { $arguments += @('--map', (Resolve-Path -LiteralPath $Map).Path) }
}
# A pipeline also waits for GUI programs, so that the exit code reaches Start.cmd.
& $engine @arguments | Out-Host
exit $LASTEXITCODE
