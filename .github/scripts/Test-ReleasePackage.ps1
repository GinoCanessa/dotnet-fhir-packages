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
    throw "Package '$fullPackagePath' does not exist."
}

[string] $expectedFileName = "$PackageId.$Version.nupkg"
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
        throw 'The package nuspec has no metadata element.'
    }

    [System.Xml.XmlElement] $id =
        $metadata.SelectSingleNode('n:id', $namespaces)
    if ($null -eq $id -or $id.InnerText -cne $PackageId)
    {
        throw "The package id is not '$PackageId'."
    }

    [System.Xml.XmlElement] $packageVersion =
        $metadata.SelectSingleNode('n:version', $namespaces)
    if ($null -eq $packageVersion -or
        $packageVersion.InnerText -cne $Version)
    {
        throw "The nuspec version does not match '$Version'."
    }

    [System.Xml.XmlElement] $repository =
        $metadata.SelectSingleNode('n:repository', $namespaces)
    if ($null -eq $repository)
    {
        throw 'The package nuspec has no repository metadata.'
    }

    if ($repository.GetAttribute('type') -cne 'git' -or
        $repository.GetAttribute('url') -cne
            'https://github.com/GinoCanessa/dotnet-fhir-packages' -or
        $repository.GetAttribute('commit') -cne $RepositoryCommit)
    {
        throw 'The package repository metadata does not match the release commit.'
    }

    [System.Collections.Generic.HashSet[string]] $entryNames =
        [System.Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries)
    {
        [void] $entryNames.Add($entry.FullName)
    }

    if ($PackageId -ceq 'fhir-pkg-lib')
    {
        foreach ($framework in @('net8.0', 'net9.0', 'net10.0'))
        {
            [string] $requiredEntry = "lib/$framework/FhirPkg.dll"
            if (!$entryNames.Contains($requiredEntry))
            {
                throw "The package is missing '$requiredEntry'."
            }
        }
    }
    else
    {
        [System.Xml.XmlNode[]] $packageTypes = @(
            $metadata.SelectNodes(
                'n:packageTypes/n:packageType',
                $namespaces))
        if ($packageTypes.Count -ne 1 -or
            $packageTypes[0].Attributes['name'].Value -cne 'DotnetTool')
        {
            throw 'The CLI package type must be DotnetTool.'
        }

        [System.Xml.XmlNode[]] $dependencies = @(
            $metadata.SelectNodes(
                'n:dependencies//n:dependency',
                $namespaces))
        if ($dependencies.Count -ne 0)
        {
            throw 'The CLI package must not declare package dependencies.'
        }

        [string[]] $requiredToolFiles = @(
            'DotnetToolSettings.xml',
            'FhirPkg.Cli.dll',
            'FhirPkg.Cli.deps.json',
            'FhirPkg.Cli.runtimeconfig.json',
            'FhirPkg.dll'
        )
        foreach ($framework in @('net8.0', 'net9.0', 'net10.0'))
        {
            [string] $toolRoot = "tools/$framework/any"
            foreach ($toolFile in $requiredToolFiles)
            {
                [string] $requiredEntry = "$toolRoot/$toolFile"
                if (!$entryNames.Contains($requiredEntry))
                {
                    throw "The package is missing '$requiredEntry'."
                }
            }

            [System.IO.Compression.ZipArchiveEntry[]] $settingsEntries = @(
                $archive.Entries |
                    Where-Object {
                        $_.FullName -ceq
                            "$toolRoot/DotnetToolSettings.xml"
                    })
            if ($settingsEntries.Count -ne 1)
            {
                throw "Expected one tool settings file for '$framework'."
            }

            [System.IO.StreamReader] $settingsReader =
                [System.IO.StreamReader]::new(
                    $settingsEntries[0].Open())
            try
            {
                [xml] $settings = $settingsReader.ReadToEnd()
            }
            finally
            {
                $settingsReader.Dispose()
            }

            [System.Xml.XmlNode[]] $commands = @(
                $settings.SelectNodes(
                    '/DotNetCliTool/Commands/Command'))
            if ($commands.Count -ne 1 -or
                $commands[0].Attributes['Name'].Value -cne 'fhir-pkg' -or
                $commands[0].Attributes['EntryPoint'].Value -cne
                    'FhirPkg.Cli.dll' -or
                $commands[0].Attributes['Runner'].Value -cne 'dotnet')
            {
                throw "The tool settings for '$framework' are invalid."
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
    Add-Content -Path $env:GITHUB_OUTPUT -Value "package_path=$fullPackagePath"
    Add-Content -Path $env:GITHUB_OUTPUT -Value "sha256=$sha256"
}

Write-Output "Verified $expectedFileName ($sha256)."
