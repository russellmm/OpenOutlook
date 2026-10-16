# Builds native/openpst (MSVC) and copies openpst.dll into src\OpenOutlook.Desktop\runtimes\win-x64\native\
param([string]$Rid = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'native\openpst'
$bld = Join-Path $src "build-$Rid"
$vcvars = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
$vcvars = Join-Path $vcvars 'VC\Auxiliary\Build\vcvars64.bat'
# The write test (and its clean-file guard: the fixer must leave Outlook-written, SCANPST-clean files byte for byte unchanged) needs real PSTs. On the
# owner's PC they are the known-clean archives below; elsewhere set OPST_TEST_PST / OPST_CLEAN_PSTS yourself, or the write test is skipped.
if (-not $env:OPST_TEST_PST -and (Test-Path 'F:\Claude\rmarrash_2.pst')) { $env:OPST_TEST_PST = 'F:\Claude\rmarrash_2.pst' }
if (-not $env:OPST_CLEAN_PSTS -and (Test-Path 'F:\Claude\rmarrash_1_clean.pst')) { $env:OPST_CLEAN_PSTS = 'F:\Claude\rmarrash_1_clean.pst' }
$cmd = "`"$vcvars`" >nul && cmake -S `"$src`" -B `"$bld`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DOPST_BUILD_TOOLS=OFF && cmake --build `"$bld`" && cd /d `"$bld`" && ctest --output-on-failure"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "native build failed ($LASTEXITCODE)" }
$dest = Join-Path $root "src\OpenOutlook.Desktop\runtimes\$Rid\native"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $bld 'openpst.dll') $dest -Force
Write-Host "native library -> $dest"
