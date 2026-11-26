# build.ps1 - compile 7D2D-ServerSearch.exe (Windows x64, .NET Framework 4 csc only)
# Prereq: put com.rlabrecque.steamworks.net.dll and steam_api64.dll into .\deps
#         (from https://steamworks.github.io/), optional custom page at .\ui\index.html
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$swnet = Join-Path $root 'deps\com.rlabrecque.steamworks.net.dll'
$api64 = Join-Path $root 'deps\steam_api64.dll'
foreach ($f in $swnet, $api64) {
    if (-not (Test-Path $f)) { throw "missing $f - download Steamworks.NET and place DLLs under .\deps" }
}

$build = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $build | Out-Null

Write-Host '[1/4] SteamList.exe'
& $csc /nologo /optimize+ /platform:x64 /target:exe "/out:$build\SteamList.exe" "/r:$swnet" "$root\src\SteamList.cs"
if ($LASTEXITCODE -ne 0) { throw 'SteamList compile failed' }

Write-Host '[2/4] packing resources'
function Gz-File($src, $dst) {
    $in = [IO.File]::OpenRead($src); $out = [IO.File]::Create($dst)
    $g = New-Object IO.Compression.GZipStream($out, [IO.Compression.CompressionLevel]::Optimal)
    $in.CopyTo($g); $g.Dispose(); $out.Dispose(); $in.Dispose()
}
Gz-File $api64 "$build\api64.gz"
Gz-File $swnet "$build\swnet.gz"
Gz-File "$build\SteamList.exe" "$build\steamlist.gz"

$ui = Join-Path $root 'ui\index.html'
if (-not (Test-Path $ui)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $ui) | Out-Null
    '<!doctype html><meta charset="utf-8"><title>7D2D Server Search</title>' +
    '<body style="font-family:sans-serif;padding:2rem">' +
    'UI placeholder. Put your own page at ui\index.html and rebuild. ' +
    'API: /api/master /api/query?addr=IP:PORT /api/ping' |
    Set-Content -Path $ui -Encoding UTF8
    Write-Host '  (generated placeholder ui\index.html)'
}
Copy-Item $ui "$build\index.bin" -Force
Set-Content -Path "$build\appid.txt" -Value '251570' -NoNewline -Encoding ascii

Write-Host '[3/4] fullapp.exe'
& $csc /nologo /optimize+ /platform:x64 /target:exe "/out:$build\7D2D-ServerSearch.exe" `
    "/resource:$build\api64.gz,api64.gz" `
    "/resource:$build\swnet.gz,swnet.gz" `
    "/resource:$build\steamlist.gz,steamlist.gz" `
    "/resource:$build\index.bin,index.bin" `
    "/resource:$build\appid.txt,appid.txt" `
    "$root\src\fullapp.cs"
if ($LASTEXITCODE -ne 0) { throw 'fullapp compile failed' }

Write-Host '[4/4] done'
Copy-Item "$build\7D2D-ServerSearch.exe" (Join-Path $root '7D2D-ServerSearch.exe') -Force
Get-Item (Join-Path $root '7D2D-ServerSearch.exe') | Select-Object Name, Length
