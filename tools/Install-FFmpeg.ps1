$ErrorActionPreference = 'Stop'
$uri = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
$expected = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$zip = Join-Path $env:TEMP 'reelgood-ffmpeg-release-essentials.zip'
$unpack = Join-Path $env:TEMP 'reelgood-ffmpeg-release-essentials'
Invoke-WebRequest -Uri $uri -OutFile $zip
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { Remove-Item -LiteralPath $zip -Force; throw "FFmpeg archive checksum mismatch. Expected $expected; got $actual." }
if (Test-Path -LiteralPath $unpack) { Remove-Item -LiteralPath $unpack -Recurse -Force }
Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
$build = Get-ChildItem -LiteralPath $unpack -Directory | Select-Object -First 1
$bin = Join-Path $build.FullName 'bin'
$target = $PSScriptRoot
Copy-Item (Join-Path $bin 'ffmpeg.exe') (Join-Path $target 'ffmpeg.exe') -Force
Copy-Item (Join-Path $bin 'ffprobe.exe') (Join-Path $target 'ffprobe.exe') -Force
Copy-Item (Join-Path $build.FullName 'LICENSE') (Join-Path $target 'FFmpeg-GPLv3-LICENSE.txt') -Force
Copy-Item (Join-Path $build.FullName 'README.txt') (Join-Path $target 'FFmpeg-build-notes.txt') -Force
Remove-Item -LiteralPath $zip -Force
Remove-Item -LiteralPath $unpack -Recurse -Force
Write-Host "Verified FFmpeg $actual and installed ffmpeg.exe + ffprobe.exe into $target"
