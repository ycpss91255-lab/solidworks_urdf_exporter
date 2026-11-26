# Installer check: the icons of the menu items must be installed in an images subdirectory beside
# SW2URDF.dll, where the add-in looks for them. The main file entry does not include subdirectories.
# Reads the installer script, the project file and the add-in source; needs no build and no SOLIDWORKS.
param([string]$Root = "$PSScriptRoot/..")
$ErrorActionPreference = 'Stop'
function Require([bool]$condition, [string]$message) { if (-not $condition) { throw "FAIL: $message" } }

$files = @(Get-Content -LiteralPath (Join-Path $Root 'INSTALL/Install.iss') -Encoding UTF8 | Where-Object { $_ -match '^\s*Source:' })
$main = @($files | Where-Object { $_ -match 'DestDir:\s*\{app\};' })
Require ($main.Count -ge 1) 'installer has no main file entry'
$images = @($files | Where-Object { $_ -match 'Source:\s*x64\\Debug\\images\\\*\.png;' -and $_ -match 'DestDir:\s*\{app\}\\images[;\s]' })
Require ($images.Count -eq 1 -or @($main | Where-Object { $_ -match 'recursesubdirs' }).Count -ge 1) 'installer does not install images\*.png into {app}\images'
if ($images.Count -eq 1) { Require ($images[0] -match 'Check:\s*IsWin64') 'the images entry must use the same 64-bit check as the main entry' }

$source = Get-Content -LiteralPath (Join-Path $Root 'SW2URDF/SW/SwAddin.cs') -Raw -Encoding UTF8
$project = Get-Content -LiteralPath (Join-Path $Root 'SW2URDF/SW2URDF.csproj') -Raw -Encoding UTF8
Require ($source -notmatch '[A-Z]:(\\\\|\\|/)Program Files') 'the add-in source names a fixed install folder'
$names = @([regex]::Matches($source, 'ros_logo_\w+\.png') | ForEach-Object Value)
Require ($names.Count -ge 1) 'the add-in names no menu icon'
foreach ($name in $names) {
    Require (Test-Path -LiteralPath (Join-Path $Root "SW2URDF/images/$name")) "$name is not in SW2URDF/images"
    Require ($project -match ('<Content Include="images\\' + [regex]::Escape($name) + '">\s*<CopyToOutputDirectory>')) "$name is not copied to the output folder"
}
Write-Output "PASS: the installer and the build put the $($names.Count) menu icons where the add-in looks for them"
