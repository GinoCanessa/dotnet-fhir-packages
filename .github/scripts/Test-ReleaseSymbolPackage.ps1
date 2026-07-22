[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('fhir-pkg-lib', 'fhir-pkg-cli')]
    [string] $PackageId,

    [Parameter(Mandatory)]
    [string] $PackagePath,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[string] $fullPackagePath = [System.IO.Path]::GetFullPath($PackagePath)
if (![System.IO.File]::Exists($fullPackagePath))
{
    throw "Symbols package '$fullPackagePath' does not exist."
}

[string] $expectedFileName = "$PackageId.$Version.snupkg"
if ([System.IO.Path]::GetFileName($fullPackagePath) -cne $expectedFileName)
{
    throw "Expected '$expectedFileName', found '$([System.IO.Path]::GetFileName($fullPackagePath))'."
}

[System.IO.Compression.ZipArchive] $archive =
    [System.IO.Compression.ZipFile]::OpenRead($fullPackagePath)

try
{
    [System.IO.Compression.ZipArchiveEntry[]] $nuspecEntries = @(
        $archive.Entries |
            Where-Object {
                $_.FullName.EndsWith(
                    '.nuspec',
                    [StringComparison]::OrdinalIgnoreCase)
            })
    if ($nuspecEntries.Count -ne 1)
    {
        throw "Expected one nuspec, found $($nuspecEntries.Count)."
    }

    [System.IO.StreamReader] $reader =
        [System.IO.StreamReader]::new($nuspecEntries[0].Open())
    try
    {
        [xml] $nuspec = $reader.ReadToEnd()
    }
    finally
    {
        $reader.Dispose()
    }

    [System.Xml.XmlNamespaceManager] $namespaces =
        [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $namespaces.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)

    [System.Xml.XmlElement] $metadata =
        $nuspec.SelectSingleNode('/n:package/n:metadata', $namespaces)
    if ($null -eq $metadata)
    {
        throw 'The symbols package nuspec has no metadata element.'
    }

    [System.Xml.XmlElement] $id =
        $metadata.SelectSingleNode('n:id', $namespaces)
    if ($null -eq $id -or $id.InnerText -cne $PackageId)
    {
        throw "The symbols package id is not '$PackageId'."
    }

    [System.Xml.XmlElement] $packageVersion =
        $metadata.SelectSingleNode('n:version', $namespaces)
    if ($null -eq $packageVersion -or
        $packageVersion.InnerText -cne $Version)
    {
        throw "The symbols package version does not match '$Version'."
    }

    [System.Xml.XmlElement] $repository =
        $metadata.SelectSingleNode('n:repository', $namespaces)
    if ($null -eq $repository -or
        $repository.GetAttribute('type') -cne 'git' -or
        $repository.GetAttribute('url') -cne
            'https://github.com/GinoCanessa/dotnet-fhir-packages' -or
        $repository.GetAttribute('commit') -cne $RepositoryCommit)
    {
        throw 'The symbols package repository metadata does not match the release commit.'
    }

    [System.Xml.XmlNode[]] $packageTypes = @(
        $metadata.SelectNodes(
            'n:packageTypes/n:packageType',
            $namespaces))
    if ($packageTypes.Count -ne 1 -or
        $packageTypes[0].Attributes['name'].Value -cne 'SymbolsPackage')
    {
        throw 'The symbols package type must be SymbolsPackage.'
    }

    [System.Collections.Generic.HashSet[string]] $entryNames =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries)
    {
        [void] $entryNames.Add($entry.FullName)
    }

    foreach ($framework in @('net8.0', 'net9.0', 'net10.0'))
    {
        [string[]] $requiredEntries = if ($PackageId -ceq 'fhir-pkg-lib')
        {
            @("lib/$framework/FhirPkg.pdb")
        }
        else
        {
            @(
                "tools/$framework/any/FhirPkg.Cli.pdb",
                "tools/$framework/any/FhirPkg.pdb"
            )
        }

        foreach ($requiredEntry in $requiredEntries)
        {
            if (!$entryNames.Contains($requiredEntry))
            {
                throw "The symbols package is missing '$requiredEntry'."
            }
        }
    }
}
finally
{
    $archive.Dispose()
}

[string] $sha256 =
    (Get-FileHash -Algorithm SHA256 -Path $fullPackagePath).
        Hash.
        ToLowerInvariant()

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content -Path $env:GITHUB_OUTPUT -Value "symbols_path=$fullPackagePath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$sha256"
}

Write-Output "Verified $expectedFileName ($sha256)."
