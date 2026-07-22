// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class ReleaseScriptContractTests
{
    private const string Version = "2099.101.1";
    private const string Tag = "v2099.101.1";
    private const string RepositoryCommit =
        "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ReleaseCandidate_AcceptsSynchronizedSdkAndCliArtifacts()
    {
        string candidateDirectory = CreateCandidate();
        try
        {
            ScriptResult result = InvokeCandidate(candidateDirectory);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain(
                "Verified synchronized release candidate");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseCandidate_RejectsCliSdkAssemblyMismatch()
    {
        string candidateDirectory =
            CreateCandidate(mismatchCliAssembly: true);
        try
        {
            ScriptResult result = InvokeCandidate(candidateDirectory);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain(
                "embedded SDK assembly for 'net9.0' does not match");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseCandidate_RejectsMissingOrUnexpectedArtifact()
    {
        string missingDirectory = CreateCandidate();
        string unexpectedDirectory = CreateCandidate();
        try
        {
            File.Delete(
                Path.Combine(
                    missingDirectory,
                    $"fhir-pkg-lib.{Version}.sha512"));
            ScriptResult missingResult =
                InvokeCandidate(missingDirectory);

            missingResult.ExitCode.ShouldNotBe(0);
            missingResult.Output.ShouldContain(
                "inventory must contain exactly");

            File.WriteAllText(
                Path.Combine(unexpectedDirectory, "unexpected.txt"),
                "unexpected",
                Encoding.ASCII);
            ScriptResult unexpectedResult =
                InvokeCandidate(unexpectedDirectory);

            unexpectedResult.ExitCode.ShouldNotBe(0);
            unexpectedResult.Output.ShouldContain(
                "inventory must contain exactly");
        }
        finally
        {
            Directory.Delete(missingDirectory, recursive: true);
            Directory.Delete(unexpectedDirectory, recursive: true);
        }
    }

    [Fact]
    public void VersionAvailability_RequiresVersionAboveBothPackageIndexes()
    {
        using PackageServer server = new();
        server.AddJson(
            "/sdk/index.json",
            """{"versions":["2099.100.0","preview-value"]}""");
        server.AddJson(
            "/cli/index.json",
            """{"versions":["2099.102.0"]}""");

        ScriptResult behindResult = InvokeScript(
            "Test-ReleaseVersionAvailability.ps1",
            string.Empty,
            "-Version",
            "2099.101.1",
            "-SdkIndexUri",
            server.GetUri("sdk/index.json").AbsoluteUri,
            "-CliIndexUri",
            server.GetUri("cli/index.json").AbsoluteUri);
        behindResult.ExitCode.ShouldNotBe(0);
        behindResult.Output.ShouldContain(
            "greater than the highest published canonical version");

        ScriptResult publishedResult = InvokeScript(
            "Test-ReleaseVersionAvailability.ps1",
            string.Empty,
            "-Version",
            "2099.102.0",
            "-SdkIndexUri",
            server.GetUri("sdk/index.json").AbsoluteUri,
            "-CliIndexUri",
            server.GetUri("cli/index.json").AbsoluteUri);
        publishedResult.ExitCode.ShouldNotBe(0);
        publishedResult.Output.ShouldContain(
            "fhir-pkg-cli '2099.102.0' is already published");

        ScriptResult freshResult = InvokeScript(
            "Test-ReleaseVersionAvailability.ps1",
            string.Empty,
            "-Version",
            "2099.103.0",
            "-SdkIndexUri",
            server.GetUri("sdk/index.json").AbsoluteUri,
            "-CliIndexUri",
            server.GetUri("cli/index.json").AbsoluteUri);
        freshResult.ExitCode.ShouldBe(0, freshResult.Output);
    }

    [Fact]
    public void PublicationState_ReportsMatchingPartialRelease()
    {
        string candidateDirectory = CreateCandidate();
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-publication-state-{Guid.NewGuid():N}.txt");
        try
        {
            using PackageServer server = new();
            server.AddBytes(
                $"/sdk/{Version}/fhir-pkg-lib.{Version}.nupkg",
                File.ReadAllBytes(
                    Path.Combine(
                        candidateDirectory,
                        $"fhir-pkg-lib.{Version}.nupkg")));

            ScriptResult result = InvokeScript(
                "Test-ReleasePublicationState.ps1",
                outputPath,
                "-CandidateDirectory",
                candidateDirectory,
                "-Version",
                Version,
                "-RepositoryCommit",
                RepositoryCommit,
                "-SdkFlatContainerUri",
                server.GetUri("sdk").AbsoluteUri,
                "-CliFlatContainerUri",
                server.GetUri("cli").AbsoluteUri,
                "-Attempts",
                "1",
                "-DelaySeconds",
                "0",
                "-SkipSignatureVerification");

            result.ExitCode.ShouldBe(0, result.Output);
            string[] outputs = File.ReadAllLines(outputPath);
            outputs.ShouldContain("cli_state=missing");
            outputs.ShouldContain("sdk_state=verified");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void PublicationState_ReportsBothPackagesMissing()
    {
        string candidateDirectory = CreateCandidate();
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-publication-state-{Guid.NewGuid():N}.txt");
        try
        {
            using PackageServer server = new();
            ScriptResult result = InvokeScript(
                "Test-ReleasePublicationState.ps1",
                outputPath,
                "-CandidateDirectory",
                candidateDirectory,
                "-Version",
                Version,
                "-RepositoryCommit",
                RepositoryCommit,
                "-SdkFlatContainerUri",
                server.GetUri("sdk").AbsoluteUri,
                "-CliFlatContainerUri",
                server.GetUri("cli").AbsoluteUri,
                "-Attempts",
                "1",
                "-DelaySeconds",
                "0",
                "-SkipSignatureVerification");

            result.ExitCode.ShouldBe(0, result.Output);
            string[] outputs = File.ReadAllLines(outputPath);
            outputs.ShouldContain("cli_state=missing");
            outputs.ShouldContain("sdk_state=missing");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void PublicationState_RejectsMismatchedExistingPackage()
    {
        string candidateDirectory = CreateCandidate();
        string publishedDirectory = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-published-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(publishedDirectory);
        string publishedPackagePath = Path.Combine(
            publishedDirectory,
            $"fhir-pkg-lib.{Version}.nupkg");
        CreateSdkPackage(
            publishedPackagePath,
            mismatchSdkAssembly: true);
        try
        {
            using PackageServer server = new();
            server.AddBytes(
                $"/sdk/{Version}/fhir-pkg-lib.{Version}.nupkg",
                File.ReadAllBytes(publishedPackagePath));

            ScriptResult result = InvokeScript(
                "Test-ReleasePublicationState.ps1",
                string.Empty,
                "-CandidateDirectory",
                candidateDirectory,
                "-Version",
                Version,
                "-RepositoryCommit",
                RepositoryCommit,
                "-SdkFlatContainerUri",
                server.GetUri("sdk").AbsoluteUri,
                "-CliFlatContainerUri",
                server.GetUri("cli").AbsoluteUri,
                "-Attempts",
                "1",
                "-DelaySeconds",
                "0",
                "-SkipSignatureVerification");

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain(
                "differs from the release candidate");
        }
        finally
        {
            Directory.Delete(candidateDirectory, recursive: true);
            Directory.Delete(publishedDirectory, recursive: true);
        }
    }

    private static ScriptResult InvokeCandidate(
        string candidateDirectory) =>
        InvokeScript(
            "Test-ReleaseCandidate.ps1",
            string.Empty,
            "-CandidateDirectory",
            candidateDirectory,
            "-Version",
            Version,
            "-Tag",
            Tag,
            "-RepositoryCommit",
            RepositoryCommit);

    private static ScriptResult InvokeScript(
        string scriptName,
        string githubOutput,
        params string[] arguments)
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "ReleaseContracts",
            "Scripts",
            scriptName);
        ProcessStartInfo startInfo = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GITHUB_OUTPUT"] = githubOutput;

        using Process process =
            Process.Start(startInfo) ??
            throw new InvalidOperationException("Unable to start pwsh.");
        Task<string> standardOutput =
            process.StandardOutput.ReadToEndAsync();
        Task<string> standardError =
            process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Release script '{scriptName}' timed out.");
        }

        string output = string.Concat(
            standardOutput.GetAwaiter().GetResult(),
            Environment.NewLine,
            standardError.GetAwaiter().GetResult());
        return new ScriptResult(process.ExitCode, output);
    }

    private static string CreateCandidate(
        bool mismatchCliAssembly = false)
    {
        string candidateDirectory = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-release-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidateDirectory);

        string sdkPackageName = $"fhir-pkg-lib.{Version}.nupkg";
        string sdkSymbolsName = $"fhir-pkg-lib.{Version}.snupkg";
        string cliPackageName = $"fhir-pkg-cli.{Version}.nupkg";
        string cliSymbolsName = $"fhir-pkg-cli.{Version}.snupkg";
        string sdkPackagePath =
            Path.Combine(candidateDirectory, sdkPackageName);
        string sdkSymbolsPath =
            Path.Combine(candidateDirectory, sdkSymbolsName);
        string cliPackagePath =
            Path.Combine(candidateDirectory, cliPackageName);
        string cliSymbolsPath =
            Path.Combine(candidateDirectory, cliSymbolsName);

        CreateSdkPackage(sdkPackagePath);
        CreateSdkSymbolsPackage(sdkSymbolsPath);
        CreateCliPackage(cliPackagePath, mismatchCliAssembly);
        CreateCliSymbolsPackage(cliSymbolsPath);
        WriteManifest(
            Path.Combine(
                candidateDirectory,
                $"fhir-pkg-lib.{Version}.sha512"),
            (sdkPackageName, sdkPackagePath),
            (sdkSymbolsName, sdkSymbolsPath));
        WriteManifest(
            Path.Combine(
                candidateDirectory,
                $"fhir-pkg-cli.{Version}.sha512"),
            (cliPackageName, cliPackagePath),
            (cliSymbolsName, cliSymbolsPath));

        PackageMetadata[] packages =
        [
            CreateMetadata(
                "fhir-pkg-lib",
                sdkPackageName,
                sdkPackagePath,
                sdkSymbolsName,
                sdkSymbolsPath),
            CreateMetadata(
                "fhir-pkg-cli",
                cliPackageName,
                cliPackagePath,
                cliSymbolsName,
                cliSymbolsPath),
        ];
        ReleaseMetadata metadata = new(
            Version,
            Tag,
            RepositoryCommit,
            "https://api.nuget.org/v3/index.json",
            packages);
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        File.WriteAllText(
            Path.Combine(candidateDirectory, "release-metadata.json"),
            JsonSerializer.Serialize(metadata, options),
            Encoding.UTF8);

        return candidateDirectory;
    }

    private static void CreateSdkPackage(
        string path,
        bool mismatchSdkAssembly = false)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-lib.nuspec",
            CreateNuspec("fhir-pkg-lib"));
        foreach (string framework in Frameworks)
        {
            byte[] assembly =
                mismatchSdkAssembly && framework == "net9.0"
                    ? Encoding.ASCII.GetBytes("mismatched-published-sdk")
                    : GetSdkAssembly(framework);
            AddBytes(
                archive,
                $"lib/{framework}/FhirPkg.dll",
                assembly);
        }
    }

    private static void CreateSdkSymbolsPackage(string path)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-lib.nuspec",
            CreateNuspec("fhir-pkg-lib", "SymbolsPackage"));
        foreach (string framework in Frameworks)
        {
            AddText(
                archive,
                $"lib/{framework}/FhirPkg.pdb",
                $"sdk-pdb-{framework}");
        }
    }

    private static void CreateCliPackage(
        string path,
        bool mismatchCliAssembly)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-cli.nuspec",
            CreateNuspec("fhir-pkg-cli", "DotnetTool"));
        foreach (string framework in Frameworks)
        {
            string toolRoot = $"tools/{framework}/any";
            AddText(
                archive,
                $"{toolRoot}/DotnetToolSettings.xml",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="1">
                  <Commands>
                    <Command Name="fhir-pkg" EntryPoint="FhirPkg.Cli.dll" Runner="dotnet" />
                  </Commands>
                </DotNetCliTool>
                """);
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.dll",
                $"cli-{framework}");
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.deps.json",
                "{}");
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.runtimeconfig.json",
                "{}");
            byte[] assembly =
                mismatchCliAssembly && framework == "net9.0"
                    ? Encoding.ASCII.GetBytes("mismatched-sdk")
                    : GetSdkAssembly(framework);
            AddBytes(
                archive,
                $"{toolRoot}/FhirPkg.dll",
                assembly);
        }
    }

    private static void CreateCliSymbolsPackage(string path)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive =
            new(stream, ZipArchiveMode.Create);
        AddText(
            archive,
            "fhir-pkg-cli.nuspec",
            CreateNuspec("fhir-pkg-cli", "SymbolsPackage"));
        foreach (string framework in Frameworks)
        {
            string toolRoot = $"tools/{framework}/any";
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.Cli.pdb",
                $"cli-pdb-{framework}");
            AddText(
                archive,
                $"{toolRoot}/FhirPkg.pdb",
                $"sdk-pdb-{framework}");
        }
    }

    private static string CreateNuspec(
        string packageId,
        string? packageType = null)
    {
        string packageTypes = packageType is null
            ? string.Empty
            : $"""
                 <packageTypes>
                   <packageType name="{packageType}" />
                 </packageTypes>
               """;
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{Version}</version>
                    <description>Release contract fixture.</description>
                {packageTypes}
                    <repository type="git" url="https://github.com/GinoCanessa/dotnet-fhir-packages" commit="{RepositoryCommit}" />
                  </metadata>
                </package>
                """;
    }

    private static void AddText(
        ZipArchive archive,
        string entryName,
        string content) =>
        AddBytes(
            archive,
            entryName,
            Encoding.UTF8.GetBytes(content));

    private static void AddBytes(
        ZipArchive archive,
        string entryName,
        byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using Stream stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] GetSdkAssembly(string framework) =>
        Encoding.ASCII.GetBytes($"sdk-{framework}");

    private static void WriteManifest(
        string manifestPath,
        (string Name, string Path) package,
        (string Name, string Path) symbols)
    {
        string[] lines =
        [
            $"{GetHash(package.Path, SHA512.HashData)}  {package.Name}",
            $"{GetHash(symbols.Path, SHA512.HashData)}  {symbols.Name}",
        ];
        File.WriteAllLines(manifestPath, lines, Encoding.ASCII);
    }

    private static PackageMetadata CreateMetadata(
        string packageId,
        string packageName,
        string packagePath,
        string symbolsName,
        string symbolsPath) =>
        new(
            packageId,
            packageName,
            symbolsName,
            GetHash(packagePath, SHA256.HashData),
            GetHash(symbolsPath, SHA256.HashData),
            GetHash(packagePath, SHA512.HashData),
            GetHash(symbolsPath, SHA512.HashData));

    private static string GetHash(
        string path,
        Func<byte[], byte[]> hash) =>
        Convert.ToHexString(hash(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static readonly string[] Frameworks =
        ["net8.0", "net9.0", "net10.0"];

    private sealed class PackageServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Dictionary<string, ServerResponse> _responses =
            new(StringComparer.Ordinal);
        private readonly Task _serverTask;

        public PackageServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            IPEndPoint endpoint =
                (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri(
                $"http://127.0.0.1:{endpoint.Port}/");
            _serverTask = RunAsync();
        }

        public Uri BaseUri { get; }

        public void AddJson(string path, string json) =>
            AddResponse(
                path,
                new ServerResponse(
                    HttpStatusCode.OK,
                    "application/json",
                    Encoding.UTF8.GetBytes(json)));

        public void AddBytes(string path, byte[] content) =>
            AddResponse(
                path,
                new ServerResponse(
                    HttpStatusCode.OK,
                    "application/octet-stream",
                    content));

        public Uri GetUri(string relativePath) =>
            new(BaseUri, relativePath.TrimStart('/'));

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            _serverTask.GetAwaiter().GetResult();
            _cancellation.Dispose();
        }

        private void AddResponse(
            string path,
            ServerResponse response)
        {
            string normalizedPath = path.StartsWith(
                "/",
                StringComparison.Ordinal)
                ? path
                : $"/{path}";
            lock (_responses)
            {
                _responses[normalizedPath] = response;
            }
        }

        private async Task RunAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(
                        _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                    when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                    when (_cancellation.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await HandleAsync(client);
                }
                catch (IOException)
                {
                    client.Dispose();
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true))
            {
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                string[] requestParts = requestLine.Split(' ');
                string method = requestParts[0];
                string target = requestParts[1];
                while (!string.IsNullOrEmpty(
                    await reader.ReadLineAsync()))
                {
                }

                string path = Uri.TryCreate(
                    target,
                    UriKind.Absolute,
                    out Uri? absoluteUri)
                    ? absoluteUri.AbsolutePath
                    : target.Split('?', 2)[0];
                ServerResponse response;
                lock (_responses)
                {
                    response = _responses.TryGetValue(
                        path,
                        out ServerResponse? configured)
                        ? configured
                        : ServerResponse.NotFound;
                }

                int statusCode = (int)response.StatusCode;
                string reason = response.StatusCode == HttpStatusCode.OK
                    ? "OK"
                    : "Not Found";
                string headers =
                    $"HTTP/1.1 {statusCode} {reason}\r\n" +
                    $"Content-Length: {response.Content.Length}\r\n" +
                    $"Content-Type: {response.ContentType}\r\n" +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(headers));
                if (!string.Equals(
                    method,
                    "HEAD",
                    StringComparison.Ordinal))
                {
                    await stream.WriteAsync(response.Content);
                }
            }
        }
    }

    private sealed record ServerResponse(
        HttpStatusCode StatusCode,
        string ContentType,
        byte[] Content)
    {
        public static ServerResponse NotFound { get; } =
            new(
                HttpStatusCode.NotFound,
                "text/plain",
                []);
    }

    private sealed record ScriptResult(int ExitCode, string Output);

    private sealed record PackageMetadata(
        string PackageId,
        string PackageFile,
        string SymbolsFile,
        string PackageSha256,
        string SymbolsSha256,
        string PackageSha512,
        string SymbolsSha512);

    private sealed record ReleaseMetadata(
        string Version,
        string Tag,
        string RepositoryCommit,
        string Feed,
        IReadOnlyList<PackageMetadata> Packages);
}
