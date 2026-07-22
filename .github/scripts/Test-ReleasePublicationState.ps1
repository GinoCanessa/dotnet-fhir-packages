[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $CandidateDirectory,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit,

    [string] $SdkFlatContainerUri =
        'https://api.nuget.org/v3-flatcontainer/fhir-pkg-lib',

    [string] $CliFlatContainerUri =
        'https://api.nuget.org/v3-flatcontainer/fhir-pkg-cli',

    [int] $Attempts = 5,

    [int] $DelaySeconds = 5,

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

[string] $fullCandidateDirectory =
    [System.IO.Path]::GetFullPath($CandidateDirectory)
if (![System.IO.Directory]::Exists($fullCandidateDirectory))
{
    throw "Candidate directory '$fullCandidateDirectory' does not exist."
}

[object[]] $packages = @(
    [pscustomobject]@{
        Role = 'cli'
        PackageId = 'fhir-pkg-cli'
        FlatContainerUri = $CliFlatContainerUri
    },
    [pscustomobject]@{
        Role = 'sdk'
        PackageId = 'fhir-pkg-lib'
        FlatContainerUri = $SdkFlatContainerUri
    }
)
[System.Collections.Generic.Dictionary[string, string]] $states =
    [System.Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
[System.Net.Http.HttpClient] $client =
    [System.Net.Http.HttpClient]::new()

try
{
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    foreach ($package in $packages)
    {
        [string] $versionPath = $Version.ToLowerInvariant()
        [string] $packageUri =
            "$($package.FlatContainerUri.TrimEnd('/'))/$versionPath/$($package.PackageId).$versionPath.nupkg"
        [bool] $visible = $false
        [bool] $resolved = $false

        for ([int] $attempt = 1; $attempt -le $Attempts; $attempt++)
        {
            [System.Net.Http.HttpRequestMessage] $request =
                [System.Net.Http.HttpRequestMessage]::new(
                    [System.Net.Http.HttpMethod]::Get,
                    $packageUri)
            try
            {
                [System.Net.Http.HttpResponseMessage] $response =
                    $client.SendAsync(
                        $request,
                        [System.Net.Http.HttpCompletionOption]::
                            ResponseHeadersRead).
                        GetAwaiter().
                        GetResult()
                try
                {
                    [int] $statusCode = [int] $response.StatusCode
                    if ($statusCode -eq 404)
                    {
                        $resolved = $true
                        break
                    }

                    if ($response.IsSuccessStatusCode)
                    {
                        $visible = $true
                        $resolved = $true
                        break
                    }

                    if ($statusCode -notin @(
                        408,
                        425,
                        429,
                        500,
                        502,
                        503,
                        504))
                    {
                        throw "Package visibility check for '$($package.PackageId)' returned HTTP $statusCode."
                    }
                }
                finally
                {
                    $response.Dispose()
                }
            }
            catch [System.Management.Automation.MethodInvocationException]
            {
                [Exception] $failure = $_.Exception.InnerException
                if ($failure -isnot
                        [System.Net.Http.HttpRequestException] -and
                    $failure -isnot
                        [System.Threading.Tasks.TaskCanceledException] -and
                    $failure -isnot [System.IO.IOException])
                {
                    throw
                }
            }
            finally
            {
                $request.Dispose()
            }

            if (!$resolved -and $attempt -lt $Attempts)
            {
                Start-Sleep -Seconds $DelaySeconds
            }
        }

        if (!$resolved)
        {
            throw "Unable to determine publication state for '$($package.PackageId)' after $Attempts attempts."
        }

        if (!$visible)
        {
            $states.Add($package.Role, 'missing')
            Write-Output "$($package.PackageId)=missing"
            continue
        }

        [string] $candidatePackagePath =
            [System.IO.Path]::Combine(
                $fullCandidateDirectory,
                "$($package.PackageId).$Version.nupkg")
        [hashtable] $verification = @{
            PackageId = $package.PackageId
            CandidatePackagePath = $candidatePackagePath
            PublishedPackageUri = $packageUri
            Version = $Version
            RepositoryCommit = $RepositoryCommit
            Attempts = $Attempts
            DelaySeconds = $DelaySeconds
        }
        if ($SkipSignatureVerification)
        {
            $verification.SkipSignatureVerification = $true
        }

        [string] $githubOutput = $env:GITHUB_OUTPUT
        try
        {
            $env:GITHUB_OUTPUT = ''
            & (Join-Path `
                $PSScriptRoot `
                'Test-PublishedReleasePackage.ps1') `
                @verification
        }
        finally
        {
            $env:GITHUB_OUTPUT = $githubOutput
        }

        $states.Add($package.Role, 'verified')
        Write-Output "$($package.PackageId)=verified"
    }
}
finally
{
    $client.Dispose()
}

if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT))
{
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "cli_state=$($states['cli'])"
    Add-Content `
        -Path $env:GITHUB_OUTPUT `
        -Value "sdk_state=$($states['sdk'])"
}

Write-Output "Publication state: CLI $($states['cli']); SDK $($states['sdk'])."
