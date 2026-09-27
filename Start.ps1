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
# minute). The build needs a .NET SDK 8 or newer; without one installed, the first start also downloads a private
# .NET SDK 10 into .tools, so nothing has to be installed. The project also opens directly in any Godot 4.7 .NET
# editor (see README.md).
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
# Every failure ends here with a plain message; Start.cmd then keeps the window open, so the message can be read.
trap {
    Write-Host ''
    Write-Host "Cartoon Terrain Studio could not start: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
Set-Location -LiteralPath $PSScriptRoot
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell downloads many times slower while it draws progress
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$tools = Join-Path $PSScriptRoot '.tools'

# -- Engine: Godot 4.7.2 .NET (downloaded to .tools when needed) --
$engineName = 'Godot_v4.7.2-stable_mono_win64'
$engineDir = Join-Path $tools $engineName
$engine = Join-Path $engineDir "$engineName.exe"
$console = Join-Path $engineDir "${engineName}_console.exe"
$engineComplete = { (Test-Path -LiteralPath $engine) -and (Test-Path -LiteralPath (Join-Path $engineDir 'GodotSharp')) }
if (-not (& $engineComplete)) {
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $archive = Join-Path $tools "$engineName.zip"
    Write-Host "Downloading $engineName (about 110 MB) ..."
    Invoke-WebRequest -Uri "https://github.com/godotengine/godot-builds/releases/download/4.7.2-stable/$engineName.zip" -OutFile $archive -UseBasicParsing
    Write-Host 'Unpacking Godot ...'
    Expand-Archive -LiteralPath $archive -DestinationPath $tools -Force
    Remove-Item -LiteralPath $archive -Force
}
if (-not (& $engineComplete)) { throw "Godot 4.7.2 .NET is missing under $engineDir." }

# -- .NET SDK 8 or newer: the installed one, else a private .NET SDK 10 in .tools --
# True when this dotnet.exe has an SDK of version 8 or newer ("dotnet --list-sdks" prints "10.0.401 [C:\...\sdk]").
function Test-DotnetSdk([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $false }
    return @(& $Path --list-sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }).Count -gt 0
}
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not (Test-DotnetSdk $dotnet)) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-DotnetSdk $dotnet)) {
    $dotnetRoot = Join-Path $tools 'dotnet'
    $dotnet = Join-Path $dotnetRoot 'dotnet.exe'
    if (-not (Test-DotnetSdk $dotnet)) {
        New-Item -ItemType Directory -Path $tools -Force | Out-Null
        Write-Host 'No .NET SDK 8 or newer is installed: downloading a private .NET SDK 10 into .tools (about 290 MB) ...'
        $installer = Join-Path $tools 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
        & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 10.0 -InstallDir $dotnetRoot -NoPath | Out-Host
        Remove-Item -LiteralPath $installer -Force
        if (-not (Test-DotnetSdk $dotnet)) {
            throw 'The .NET SDK download failed. Install the .NET SDK 8 or newer from https://dotnet.microsoft.com/download and start again.'
        }
    }
    $env:DOTNET_ROOT = $dotnetRoot   # Godot loads the .NET runtime from the private copy as well
}
# Godot's editor (the first import, -Editor) finds the SDK through PATH.
$env:PATH = "$(Split-Path -Parent $dotnet);$env:PATH"

# -- C# build (Godot loads the Debug configuration, which this project builds optimised) --
if (-not $NoBuild) {
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'   # build messages in English on any system language
    $env:DOTNET_NOLOGO = '1'             # no welcome banner on the first use of a fresh SDK
    & $dotnet build (Join-Path $PSScriptRoot 'CartoonTerrainStudio.csproj') -c Debug -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The C# build failed (see the messages above).' }
}

# -- First start: Godot imports shaders and textures once --
$imported = Join-Path $PSScriptRoot '.godot\imported'
if (-not (Test-Path -LiteralPath $imported)) {
    Write-Host 'First start: Godot is importing the project ...'
    & $console --headless --path $PSScriptRoot --import | Out-Host
    if (-not (Test-Path -LiteralPath $imported)) { throw 'Godot could not import the project (see the messages above).' }
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
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    Write-Host ''
    Write-Host "Godot ended with exit code $exitCode. Its log: $env:APPDATA\Godot\app_userdata\Cartoon Terrain Studio\logs\godot.log" -ForegroundColor Red
}
exit $exitCode
