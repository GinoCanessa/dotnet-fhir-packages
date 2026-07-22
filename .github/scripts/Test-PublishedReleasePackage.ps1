[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CandidatePackagePath,

    [Parameter(Mandatory)]
    [string] $PublishedPackageUri,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit,

    [int] $Attempts = 45,

    [int] $DelaySeconds = 20,

    [switch] $SkipSignatureVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Attempts -lt 1)
{
    throw 'Attempts must be at least one.'
}

if ($DelaySeconds -lt 0)
{
    throw 'DelaySeconds cannot be negative.'
}

[string] $fullCandidatePath =
    [System.IO.Path]::GetFullPath($CandidatePackagePath)
if (![System.IO.File]::Exists($fullCandidatePath))
{
    throw "Candidate package '$fullCandidatePath' does not exist."
}

[string] $temporaryDirectory = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "fhirpkg-published-$([Guid]::NewGuid().ToString('N'))")
[System.IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
[string] $publishedPath = [System.IO.Path]::Combine(
    $temporaryDirectory,
    "fhir-pkg-lib.$Version.nupkg")

try
{
    [System.Net.Http.HttpClient] $client =
        [System.Net.Http.HttpClient]::new()
    try
    {
        $client.Timeout = [TimeSpan]::FromSeconds(30)
        for ([int] $attempt = 1; $attempt -le $Attempts; $attempt++)
        {
            try
            {
                [System.Threading.Tasks.Task[byte[]]] $request =
                    $client.GetByteArrayAsync($PublishedPackageUri)
                [byte[]] $content =
                    $request.GetAwaiter().GetResult()
                if ($content.Length -eq 0)
                {
                    throw [System.IO.InvalidDataException]::new(
                        'Published package download was empty.')
                }

                [System.IO.File]::WriteAllBytes(
                    $publishedPath,
                    $content)
                break
            }
            catch [System.Management.Automation.MethodInvocationException]
            {
                [Exception] $failure =
                    $_.Exception.InnerException
                if ($failure -is
                        [System.Net.Http.HttpRequestException])
                {
                    $statusCode = $failure.StatusCode
                    if ($null -ne $statusCode -and
                        [int] $statusCode -notin @(
                            404,
                            408,
                            425,
                            429,
                            500,
                            502,
                            503,
                            504))
                    {
                        throw
                    }
                }
                elseif ($failure -isnot
                        [System.Threading.Tasks.TaskCanceledException] -and
                    $failure -isnot [System.IO.IOException])
                {
                    throw
                }
            }
            catch [System.Threading.Tasks.TaskCanceledException]
            {
            }
            catch [System.IO.IOException]
            {
            }

            if ([System.IO.File]::Exists($publishedPath))
            {
                [System.IO.File]::Delete($publishedPath)
            }

            if ($attempt -lt $Attempts)
            {
                Start-Sleep -Seconds $DelaySeconds
            }
        }
    }
    finally
    {
        $client.Dispose()
    }

    if (![System.IO.File]::Exists($publishedPath))
    {
        throw "Published package was not available after $Attempts attempts."
    }

    if (!$SkipSignatureVerification)
    {
        & dotnet nuget verify `
            --all `
            --verbosity minimal `
            $publishedPath
        if ($LASTEXITCODE -ne 0)
        {
            throw 'Published package signature verification failed.'
        }
    }

    [string] $githubOutput = $env:GITHUB_OUTPUT
    try
    {
        $env:GITHUB_OUTPUT = ''
        & (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') `
            -PackagePath $publishedPath `
            -Version $Version `
            -RepositoryCommit $RepositoryCommit
    }
    finally
    {
        $env:GITHUB_OUTPUT = $githubOutput
    }

    function Get-ContentHashes([string] $packagePath)
    {
        [System.Collections.Generic.Dictionary[string, string]] $hashes =
            [System.Collections.Generic.Dictionary[string, string]]::new(
                [StringComparer]::Ordinal)
        [System.IO.Compression.ZipArchive] $archive =
            [System.IO.Compression.ZipFile]::OpenRead($packagePath)
        try
        {
            foreach ($entry in $archive.Entries)
            {
                if ($entry.FullName -iin @(
                    '.signature.p7s',
                    '_rels/.rels',
                    '[Content_Types].xml'))
                {
                    continue
                }

                [System.IO.Stream] $stream = $entry.Open()
                try
                {
                    [byte[]] $hash =
                        [System.Security.Cryptography.SHA256]::HashData($stream)
                    $hashes.Add(
                        $entry.FullName,
                        [Convert]::ToHexString($hash).ToLowerInvariant())
                }
                finally
                {
                    $stream.Dispose()
                }
            }
        }
        finally
        {
            $archive.Dispose()
        }

        return $hashes
    }

    [System.Collections.Generic.Dictionary[string, string]] $candidateHashes =
        Get-ContentHashes $fullCandidatePath
    [System.Collections.Generic.Dictionary[string, string]] $publishedHashes =
        Get-ContentHashes $publishedPath
    if ($candidateHashes.Count -ne $publishedHashes.Count)
    {
        throw 'Published package entries do not match the release candidate.'
    }

    foreach ($entry in $candidateHashes.GetEnumerator())
    {
        if (!$publishedHashes.ContainsKey($entry.Key) -or
            $publishedHashes[$entry.Key] -cne $entry.Value)
        {
            throw "Published package entry '$($entry.Key)' differs from the release candidate."
        }
    }

    [string] $publishedSha256 =
        (Get-FileHash -Algorithm SHA256 -Path $publishedPath).Hash.ToLowerInvariant()
    if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
    {
        Add-Content `
            -Path $env:GITHUB_OUTPUT `
            -Value "published_sha256=$publishedSha256"
    }

    Write-Output "Verified published release package $Version ($publishedSha256)."
}
finally
{
    if ([System.IO.Directory]::Exists($temporaryDirectory))
    {
        [System.IO.Directory]::Delete(
            $temporaryDirectory,
            $true)
    }
}
