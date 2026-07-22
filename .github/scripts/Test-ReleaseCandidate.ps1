[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CandidateDirectory,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Tag,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit,

    [string] $ExpectedSdkPackageSha256,

    [string] $ExpectedCliPackageSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Sha512Manifest(
    [string] $ManifestPath,
    [string[]] $ExpectedNames)
{
    [string[]] $manifestLines = @(
        Get-Content -Path $ManifestPath |
            Where-Object { ![string]::IsNullOrWhiteSpace($_) })
    if ($manifestLines.Count -ne $ExpectedNames.Count)
    {
        throw "Expected $($ExpectedNames.Count) SHA-512 manifest entries in '$ManifestPath', found $($manifestLines.Count)."
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
                [System.Text.RegularExpressions.RegexOptions]::
                    CultureInvariant)
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

    foreach ($name in $ExpectedNames)
    {
        if (!$manifest.ContainsKey($name))
        {
            throw "SHA-512 manifest is missing '$name'."
        }
    }

    return ,$manifest
}

function Get-ZipEntrySha256(
    [string] $PackagePath,
    [string] $EntryName)
{
    [System.IO.Compression.ZipArchive] $archive =
        [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try
    {
        [System.IO.Compression.ZipArchiveEntry[]] $entries = @(
            $archive.Entries |
                Where-Object { $_.FullName -ceq $EntryName })
        if ($entries.Count -ne 1)
        {
            throw "Expected one '$EntryName' entry in '$PackagePath'."
        }

        [System.IO.Stream] $stream = $entries[0].Open()
        try
        {
            [byte[]] $hash =
                [System.Security.Cryptography.SHA256]::HashData($stream)
            return [Convert]::ToHexString($hash).ToLowerInvariant()
        }
        finally
        {
            $stream.Dispose()
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

[string] $fullCandidateDirectory =
    [System.IO.Path]::GetFullPath($CandidateDirectory)
if (![System.IO.Directory]::Exists($fullCandidateDirectory))
{
    throw "Candidate directory '$fullCandidateDirectory' does not exist."
}

[object[]] $packages = @(
    [pscustomobject]@{
        Role = 'sdk'
        PackageId = 'fhir-pkg-lib'
        PackageName = "fhir-pkg-lib.$Version.nupkg"
        SymbolsName = "fhir-pkg-lib.$Version.snupkg"
        ManifestName = "fhir-pkg-lib.$Version.sha512"
        ExpectedSha256 = $ExpectedSdkPackageSha256
    },
    [pscustomobject]@{
        Role = 'cli'
        PackageId = 'fhir-pkg-cli'
        PackageName = "fhir-pkg-cli.$Version.nupkg"
        SymbolsName = "fhir-pkg-cli.$Version.snupkg"
        ManifestName = "fhir-pkg-cli.$Version.sha512"
        ExpectedSha256 = $ExpectedCliPackageSha256
    }
)

[string[]] $expectedFiles = @(
    $packages[0].PackageName,
    $packages[0].SymbolsName,
    $packages[0].ManifestName,
    $packages[1].PackageName,
    $packages[1].SymbolsName,
    $packages[1].ManifestName,
    'release-metadata.json'
)
[string[]] $actualFiles = @(
    [System.IO.Directory]::GetFiles(
        $fullCandidateDirectory,
        '*',
        [System.IO.SearchOption]::AllDirectories) |
        ForEach-Object {
            [System.IO.Path]::GetRelativePath(
                $fullCandidateDirectory,
                $_).Replace('\', '/')
        } |
        Sort-Object)
[string[]] $sortedExpectedFiles = @($expectedFiles | Sort-Object)
if ([string]::Join("`n", $actualFiles) -cne
    [string]::Join("`n", $sortedExpectedFiles))
{
    throw "Release candidate inventory must contain exactly: $([string]::Join(', ', $sortedExpectedFiles)). Found: $([string]::Join(', ', $actualFiles))."
}

[System.Collections.Generic.Dictionary[string, object]] $results =
    [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
[string] $githubOutput = $env:GITHUB_OUTPUT
foreach ($package in $packages)
{
    [string] $packagePath =
        [System.IO.Path]::Combine(
            $fullCandidateDirectory,
            $package.PackageName)
    [string] $symbolsPath =
        [System.IO.Path]::Combine(
            $fullCandidateDirectory,
            $package.SymbolsName)
    [string] $manifestPath =
        [System.IO.Path]::Combine(
            $fullCandidateDirectory,
            $package.ManifestName)

    try
    {
        $env:GITHUB_OUTPUT = ''
        & (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') `
            -PackageId $package.PackageId `
            -PackagePath $packagePath `
            -Version $Version `
            -RepositoryCommit $RepositoryCommit
        & (Join-Path $PSScriptRoot 'Test-ReleaseSymbolPackage.ps1') `
            -PackageId $package.PackageId `
            -PackagePath $symbolsPath `
            -Version $Version `
            -RepositoryCommit $RepositoryCommit
    }
    finally
    {
        $env:GITHUB_OUTPUT = $githubOutput
    }

    [System.Collections.Generic.Dictionary[string, string]] $manifest =
        Read-Sha512Manifest `
            -ManifestPath $manifestPath `
            -ExpectedNames @(
                $package.PackageName,
                $package.SymbolsName)
    foreach ($name in @($package.PackageName, $package.SymbolsName))
    {
        [string] $path =
            [System.IO.Path]::Combine($fullCandidateDirectory, $name)
        [string] $actualSha512 =
            (Get-FileHash -Algorithm SHA512 -Path $path).
                Hash.
                ToLowerInvariant()
        if ($actualSha512 -cne $manifest[$name])
        {
            throw "SHA-512 mismatch for '$name'."
        }
    }

    [string] $packageSha256 =
        (Get-FileHash -Algorithm SHA256 -Path $packagePath).
            Hash.
            ToLowerInvariant()
    [string] $symbolsSha256 =
        (Get-FileHash -Algorithm SHA256 -Path $symbolsPath).
            Hash.
            ToLowerInvariant()
    if (![string]::IsNullOrWhiteSpace($package.ExpectedSha256) -and
        $packageSha256 -cne $package.ExpectedSha256)
    {
        throw "$($package.PackageId) SHA-256 '$packageSha256' does not match '$($package.ExpectedSha256)'."
    }

    $results.Add(
        $package.PackageId,
        [pscustomobject]@{
            Role = $package.Role
            PackagePath = $packagePath
            SymbolsPath = $symbolsPath
            ManifestPath = $manifestPath
            PackageSha256 = $packageSha256
            SymbolsSha256 = $symbolsSha256
            PackageSha512 = $manifest[$package.PackageName]
            SymbolsSha512 = $manifest[$package.SymbolsName]
        })
}

[string] $metadataPath =
    [System.IO.Path]::Combine(
        $fullCandidateDirectory,
        'release-metadata.json')
[object] $metadata =
    Get-Content -Raw -Path $metadataPath |
        ConvertFrom-Json
if ($metadata.version -cne $Version -or
    $metadata.tag -cne $Tag -or
    $metadata.repositoryCommit -cne $RepositoryCommit -or
    $metadata.feed -cne 'https://api.nuget.org/v3/index.json')
{
    throw 'Release metadata identity does not match the candidate.'
}

[object[]] $metadataPackages = @($metadata.packages)
if ($metadataPackages.Count -ne 2)
{
    throw "Release metadata must contain two package records, found $($metadataPackages.Count)."
}

foreach ($package in $packages)
{
    [object[]] $matches = @(
        $metadataPackages |
            Where-Object { $_.packageId -ceq $package.PackageId })
    if ($matches.Count -ne 1)
    {
        throw "Release metadata must contain one '$($package.PackageId)' record."
    }

    [object] $record = $matches[0]
    [object] $result = $results[$package.PackageId]
    if ($record.packageFile -cne $package.PackageName -or
        $record.symbolsFile -cne $package.SymbolsName -or
        $record.packageSha256 -cne $result.PackageSha256 -or
        $record.symbolsSha256 -cne $result.SymbolsSha256 -or
        $record.packageSha512 -cne $result.PackageSha512 -or
        $record.symbolsSha512 -cne $result.SymbolsSha512)
    {
        throw "Release metadata hashes or filenames do not match '$($package.PackageId)'."
    }
}

[object] $sdkResult = $results['fhir-pkg-lib']
[object] $cliResult = $results['fhir-pkg-cli']
foreach ($framework in @('net8.0', 'net9.0', 'net10.0'))
{
    [string] $sdkHash =
        Get-ZipEntrySha256 `
            -PackagePath $sdkResult.PackagePath `
            -EntryName "lib/$framework/FhirPkg.dll"
    [string] $cliHash =
        Get-ZipEntrySha256 `
            -PackagePath $cliResult.PackagePath `
            -EntryName "tools/$framework/any/FhirPkg.dll"
    if ($sdkHash -cne $cliHash)
    {
        throw "The CLI embedded SDK assembly for '$framework' does not match the SDK package."
    }
}

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "sdk_package_path=$($sdkResult.PackagePath)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "sdk_symbols_path=$($sdkResult.SymbolsPath)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "sdk_manifest_path=$($sdkResult.ManifestPath)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "sdk_sha256=$($sdkResult.PackageSha256)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "cli_package_path=$($cliResult.PackagePath)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "cli_symbols_path=$($cliResult.SymbolsPath)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "cli_manifest_path=$($cliResult.ManifestPath)"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "cli_sha256=$($cliResult.PackageSha256)"
}

Write-Output "Verified synchronized release candidate $Version (SDK $($sdkResult.PackageSha256), CLI $($cliResult.PackageSha256))."
