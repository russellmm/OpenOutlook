$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'native\openpst'
$bld = Join-Path $src 'build-tools'
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
$vc = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
cmd /c "`"$vc`" >nul && cmake -S `"$src`" -B `"$bld`" -G Ninja -DCMAKE_BUILD_TYPE=Release && cmake --build `"$bld`""
