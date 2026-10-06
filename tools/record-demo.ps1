# Records the scripted demo (the app's own windows on a 16:9 stage) and writes demo.mp4 + demo.gif
# into the project folder. Needs ffmpeg on PATH. Only the stage rectangle is captured, and the
# stage is an opaque top-most backdrop, so nothing else on your screen ends up in the clip.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\record-demo.ps1 [-Grabber ddagrab|gdigrab] [-TempDir <dir>]
param(
    [ValidateSet('ddagrab', 'gdigrab')][string]$Grabber = 'ddagrab',
    [string]$TempDir = (Join-Path $env:TEMP 'WindowsWithWeight-demo'),
    [double]$Trim = 1.1,        # seconds of empty stage cut from the start (ffmpeg warm-up)
    [int]$GifWidth = 720
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $project 'bin\Release\net10.0-windows\WindowsWithWeight.exe'
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'ffmpeg is not on PATH' }
if (-not (Test-Path $exe)) { & dotnet build (Join-Path $project 'WindowsWithWeight.csproj') -c Release | Out-Host }

New-Item -ItemType Directory -Force $TempDir | Out-Null
$raw = Join-Path $TempDir 'demo-raw.mp4'
$palette = Join-Path $TempDir 'palette.png'

& $exe --demo --record $raw --grabber $Grabber | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $raw)) { throw "demo recording failed ($LASTEXITCODE)" }

$mp4 = Join-Path $project 'demo.mp4'
$gif = Join-Path $project 'demo.gif'
& ffmpeg -v error -y -ss $Trim -i $raw -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart $mp4
& ffmpeg -v error -y -ss $Trim -i $raw -vf "fps=25,scale=${GifWidth}:-1:flags=lanczos,palettegen=max_colors=160:stats_mode=diff" $palette
& ffmpeg -v error -y -ss $Trim -i $raw -i $palette -lavfi "fps=25,scale=${GifWidth}:-1:flags=lanczos [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" $gif

Get-Item $mp4, $gif | ForEach-Object { '{0}  {1:N0} KB' -f $_.FullName, ($_.Length / 1KB) }
