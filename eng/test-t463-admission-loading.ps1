<# Production admission loader regression. Fresh processes prevent loaded assemblies masking failures. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AssemblyDirectory,
    [Parameter(Mandatory)][string]$NativeLibrary
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $AssemblyDirectory).Path
$native = (Resolve-Path -LiteralPath $NativeLibrary).Path
try {
    [System.Reflection.AssemblyName]::GetAssemblyName($native) | Out-Null
    throw 'NativeLibrary must be an actual native binary, not a managed assembly.'
} catch [System.BadImageFormatException] { }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('t463-loader-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
Get-ChildItem -LiteralPath $source -Filter '*.dll' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $scratch
}
Copy-Item -LiteralPath $native -Destination (Join-Path $scratch 'native-regression.dll')
$probe = Join-Path $PSScriptRoot 'test-t463-fixture-admission.ps1'
function Run-Probe([bool]$mustPass, [string]$label) {
    $output = & pwsh -NoProfile -File $probe -AssemblyDirectory $scratch 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($mustPass) {
        if ($exitCode -ne 0 -or $output -notmatch 'PASS required admission regressions') {
            throw "$label failed (exit $exitCode): $output"
        }
    } elseif ($exitCode -eq 0 -or $output -match 'PASS required admission regressions') {
        throw "$label must fail without reporting successful admission: $output"
    }
    Write-Output "PASS $label (exit $exitCode)"
}
Run-Probe $true 'native DLL coexists with valid production parser dependencies'
$producer = Join-Path $scratch 'Harborline.Blocks.BuilderDefinitions.dll'
Move-Item -LiteralPath $producer -Destination (Join-Path $scratch 'producer.saved')
Run-Probe $false 'missing required producer refuses'
Copy-Item -LiteralPath $native -Destination $producer
Run-Probe $false 'native binary substituted for required managed producer refuses'
# Scratch is retained for inspection; contains only copied binary test inputs, no operational data.
