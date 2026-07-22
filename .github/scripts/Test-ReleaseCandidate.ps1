[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CandidateDirectory,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit,

    [string] $ExpectedPackageSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[string] $fullCandidateDirectory =
    [System.IO.Path]::GetFullPath($CandidateDirectory)
if (![System.IO.Directory]::Exists($fullCandidateDirectory))
{
    throw "Candidate directory '$fullCandidateDirectory' does not exist."
}

[string] $packageName = "fhir-pkg-lib.$Version.nupkg"
[string] $symbolsName = "fhir-pkg-lib.$Version.snupkg"
[string] $manifestName = "fhir-pkg-lib.$Version.sha512"
[string] $packagePath =
    [System.IO.Path]::Combine($fullCandidateDirectory, $packageName)
[string] $symbolsPath =
    [System.IO.Path]::Combine($fullCandidateDirectory, $symbolsName)
[string] $manifestPath =
    [System.IO.Path]::Combine($fullCandidateDirectory, $manifestName)

[string] $githubOutput = $env:GITHUB_OUTPUT
try
{
    $env:GITHUB_OUTPUT = ''
    & (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') `
        -PackagePath $packagePath `
        -Version $Version `
        -RepositoryCommit $RepositoryCommit
}
finally
{
    $env:GITHUB_OUTPUT = $githubOutput
}

if (![System.IO.File]::Exists($symbolsPath))
{
    throw "Symbols package '$symbolsPath' does not exist."
}

if (![System.IO.File]::Exists($manifestPath))
{
    throw "SHA-512 manifest '$manifestPath' does not exist."
}

[string[]] $manifestLines = @(
    Get-Content -Path $manifestPath |
        Where-Object { ![string]::IsNullOrWhiteSpace($_) })
if ($manifestLines.Count -ne 2)
{
    throw "Expected two SHA-512 manifest entries, found $($manifestLines.Count)."
}

[System.Collections.Generic.Dictionary[string, string]] $manifest =
    [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
foreach ($line in $manifestLines)
{
    [System.Text.RegularExpressions.Match] $match =
        [System.Text.RegularExpressions.Regex]::Match(
            $line,
            '^(?<hash>[0-9a-f]{128})  (?<name>[^\\/]+)$',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (!$match.Success)
    {
        throw "Invalid SHA-512 manifest entry '$line'."
    }

    [string] $name = $match.Groups['name'].Value
    if (!$manifest.TryAdd(
            $name,
            $match.Groups['hash'].Value))
    {
        throw "Duplicate SHA-512 manifest entry '$name'."
    }
}

foreach ($name in @($packageName, $symbolsName))
{
    if (!$manifest.ContainsKey($name))
    {
        throw "SHA-512 manifest is missing '$name'."
    }

    [string] $path =
        [System.IO.Path]::Combine($fullCandidateDirectory, $name)
    [string] $actualSha512 =
        (Get-FileHash -Algorithm SHA512 -Path $path).Hash.ToLowerInvariant()
    if ($actualSha512 -cne $manifest[$name])
    {
        throw "SHA-512 mismatch for '$name'."
    }
}

[string] $packageSha256 =
    (Get-FileHash -Algorithm SHA256 -Path $packagePath).Hash.ToLowerInvariant()
if (![string]::IsNullOrWhiteSpace($ExpectedPackageSha256) -and
    $packageSha256 -cne $ExpectedPackageSha256)
{
    throw "Package SHA-256 '$packageSha256' does not match '$ExpectedPackageSha256'."
}

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content -Path $env:GITHUB_OUTPUT -Value "package_path=$packagePath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "symbols_path=$symbolsPath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "manifest_path=$manifestPath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$packageSha256"
}

Write-Output "Verified release candidate $Version ($packageSha256)."
