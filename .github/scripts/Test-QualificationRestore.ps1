[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AssetsPath,

    [Parameter(Mandatory)]
    [string] $GlobalPackagesPath,

    [Parameter(Mandatory)]
    [string] $CandidatePackagePath,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Framework,

    [Parameter(Mandatory)]
    [string] $ExpectedPackageSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Framework -notin @('net8.0', 'net9.0', 'net10.0'))
{
    throw "Unsupported qualification framework '$Framework'."
}

[string] $fullAssetsPath = [System.IO.Path]::GetFullPath($AssetsPath)
if (![System.IO.File]::Exists($fullAssetsPath))
{
    throw "Restore assets '$fullAssetsPath' do not exist."
}

[object] $assets =
    Get-Content -Raw -Path $fullAssetsPath |
        ConvertFrom-Json
[string] $libraryName = "fhir-pkg-lib/$Version"
[System.Management.Automation.PSPropertyInfo[]] $projectLibraries = @(
    $assets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -eq 'project' })
foreach ($projectLibrary in $projectLibraries)
{
    [string] $projectName =
        $projectLibrary.Name.Split('/', 2)[0]
    [System.Management.Automation.PSPropertyInfo] $pathProperty =
        $projectLibrary.Value.PSObject.Properties['path']
    [string] $projectPath = if ($null -eq $pathProperty)
    {
        ''
    }
    else
    {
        [string] $pathProperty.Value
    }
    [string] $normalizedProjectPath =
        $projectPath.Replace('\', '/')
    if ($projectName -ieq 'FhirPkg' -or
        $projectName -ieq 'fhir-pkg-lib' -or
        $normalizedProjectPath.EndsWith(
            '/src/FhirPkg/FhirPkg.csproj',
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Restore unexpectedly contains the FhirPkg project reference.'
    }
}

[System.Management.Automation.PSPropertyInfo] $library =
    $assets.libraries.PSObject.Properties[$libraryName]
if ($null -eq $library -or $library.Value.type -cne 'package')
{
    throw "Restore assets do not contain exact package '$libraryName'."
}

[System.Management.Automation.PSPropertyInfo[]] $targets = @(
    $assets.targets.PSObject.Properties |
        Where-Object {
            $_.Name -ceq $Framework -or
            $_.Name.StartsWith(
                "$Framework/",
                [StringComparison]::Ordinal)
        })
if ($targets.Count -ne 1)
{
    throw "Expected one restore target for '$Framework', found $($targets.Count)."
}

[System.Management.Automation.PSPropertyInfo] $targetLibrary =
    $targets[0].Value.PSObject.Properties[$libraryName]
if ($null -eq $targetLibrary)
{
    throw "Restore target '$Framework' does not reference '$libraryName'."
}

[string] $expectedAsset = "lib/$Framework/FhirPkg.dll"
[string[]] $compileAssets = @(
    $targetLibrary.Value.compile.PSObject.Properties.Name)
[string[]] $runtimeAssets = @(
    $targetLibrary.Value.runtime.PSObject.Properties.Name)
if ($expectedAsset -cnotin $compileAssets -or
    $expectedAsset -cnotin $runtimeAssets)
{
    throw "Restore target '$Framework' did not select '$expectedAsset'."
}

[string] $restoredPackagePath = [System.IO.Path]::Combine(
    [System.IO.Path]::GetFullPath($GlobalPackagesPath),
    'fhir-pkg-lib',
    $Version,
    "fhir-pkg-lib.$Version.nupkg")
if (![System.IO.File]::Exists($restoredPackagePath))
{
    throw "Restored package '$restoredPackagePath' does not exist."
}

[string] $candidateHash =
    (Get-FileHash -Algorithm SHA256 -Path $CandidatePackagePath).Hash.ToLowerInvariant()
[string] $restoredHash =
    (Get-FileHash -Algorithm SHA256 -Path $restoredPackagePath).Hash.ToLowerInvariant()
if ($candidateHash -cne $ExpectedPackageSha256 -or
    $restoredHash -cne $ExpectedPackageSha256)
{
    throw "Candidate/restored package hashes do not match '$ExpectedPackageSha256'."
}

Write-Output "Verified $libraryName uses $expectedAsset ($restoredHash)."
