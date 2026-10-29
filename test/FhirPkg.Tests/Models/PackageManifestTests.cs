// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

[Collection("EnvironmentVariable")]
public class PackageManifestTests
{
    private const string FullManifestJson = """
        {
            "name": "hl7.fhir.r4.core",
            "version": "4.0.1",
            "description": "FHIR R4 Core definitions",
            "license": "CC0-1.0",
            "author": "HL7 International",
            "homepage": "https://hl7.org/fhir/R4/",
            "canonical": "http://hl7.org/fhir",
            "fhirVersions": ["4.0.1"],
            "type": "fhir.core",
            "title": "FHIR R4",
            "dependencies": {
                "hl7.fhir.r4.expansions": "4.0.1"
            },
            "keywords": ["fhir", "r4"]
        }
        """;

    private const string MinimalManifestJson = """
        {
            "name": "minimal.package",
            "version": "1.0.0"
        }
        """;

    [Fact]
    public void Deserialize_FullManifest_AllFieldsPopulated()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Name.ShouldBe("hl7.fhir.r4.core");
        manifest.Version.ShouldBe("4.0.1");
        manifest.Description.ShouldBe("FHIR R4 Core definitions");
        manifest.License.ShouldBe("CC0-1.0");
        manifest.Author.ShouldBe("HL7 International");
        manifest.Homepage.ShouldBe("https://hl7.org/fhir/R4/");
        manifest.Canonical.ShouldBe("http://hl7.org/fhir");
        manifest.FhirVersions!.ShouldContain("4.0.1");
        manifest.Type.ShouldBe("fhir.core");
        manifest.Title.ShouldBe("FHIR R4");
        manifest.Keywords!.ShouldContain("fhir");
    }

    [Fact]
    public void Deserialize_MinimalManifest_OnlyRequiredFields()
    {
        PackageManifest manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.Name.ShouldBe("minimal.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Description.ShouldBeNull();
        manifest.Dependencies.ShouldBeNull();
        manifest.FhirVersions.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_WithDependencies_ParsedCorrectly()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Dependencies.ShouldNotBeNull();
        manifest.Dependencies.Keys.ShouldContain("hl7.fhir.r4.expansions");
        manifest.Dependencies!["hl7.fhir.r4.expansions"].ShouldBe("4.0.1");
    }

    [Fact]
    public void Deserialize_CaseInsensitive_HandlesVariations()
    {
        string json = """
            {
                "Name": "case.test",
                "Version": "2.0.0",
                "Description": "Case test"
            }
            """;

        PackageManifest manifest = PackageManifest.Deserialize(json);

        manifest.Name.ShouldBe("case.test");
        manifest.Version.ShouldBe("2.0.0");
    }

    [Fact]
    public void InferredFhirRelease_FromFhirVersions_Correct()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.InferredFhirRelease.ShouldBe(FhirRelease.R4);
    }

    [Fact]
    public void InferredFhirRelease_FromDependencies_WhenNoFhirVersions()
    {
        string json = """
            {
                "name": "test.package",
                "version": "1.0.0",
                "dependencies": {
                    "hl7.fhir.r5.core": "5.0.0"
                }
            }
            """;

        PackageManifest manifest = PackageManifest.Deserialize(json);

        manifest.InferredFhirRelease.ShouldBe(FhirRelease.R5);
    }

    [Fact]
    public void InferredFhirRelease_NoInfo_ReturnsNull()
    {
        PackageManifest manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.InferredFhirRelease.ShouldBeNull();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        string serialized = manifest.Serialize();
        PackageManifest deserialized = PackageManifest.Deserialize(serialized);

        deserialized.Name.ShouldBe(manifest.Name);
        deserialized.Version.ShouldBe(manifest.Version);
        deserialized.Description.ShouldBe(manifest.Description);
        deserialized.License.ShouldBe(manifest.License);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        Func<PackageManifest> act = () => PackageManifest.Deserialize("not valid json");

        Should.Throw<JsonException>(() => act());
    }

    [Fact]
    public void SemVer_ParsesCorrectly()
    {
        PackageManifest manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.SemVer.ShouldNotBeNull();
        manifest.SemVer!.Major.ShouldBe(4);
        manifest.SemVer.Minor.ShouldBe(0);
        manifest.SemVer.Build.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deserialize_FileStream_RetriesReadsButNotMalformedJson(bool malformed)
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-replay-{Guid.NewGuid():N}.json");
        try
        {
            string json = malformed ? """{"name":"bad","version":}""" : MinimalManifestJson;
            File.WriteAllText(path, "skip" + json);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
            primitives.UseSystemFiles();
            primitives.Enqueue<int>("ReadBorrowed", call =>
            {
                call.Stream!.Read(call.Buffer.Span[..2]).ShouldBe(2);
                return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "ReadBorrowed", path);
            });
            FileSystemContext context = primitives.CreateContext(retries: 1);

            if (malformed)
            {
                JsonException exception = Should.Throw<JsonException>(() => PackageManifest.Deserialize(caller, context));
                FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
            }
            else
            {
                PackageManifest.Deserialize(caller, context).Name.ShouldBe("minimal.package");
            }

            caller.CanRead.ShouldBeTrue();
            caller.Position.ShouldBe(caller.Length);
            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("ReadBorrowed").ShouldBe(3); // Failed block, recovered bytes, EOF; no parser restart.
            primitives.Count("SetPosition").ShouldBe(1);
            primitives.Calls.Single(call => call.Operation == "SetPosition").Offset.ShouldBe(4);
            primitives.Count("CloseFile").ShouldBe(0);
            primitives.Waits.ShouldBe([10]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_PublicFileStreamPreservesCurrentPositionAndOwnership()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-public-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "skip" + MinimalManifestJson);
            using FileStream caller = File.OpenRead(path);
            caller.Position = 4;

            PackageManifest manifest = PackageManifest.Deserialize(caller);

            manifest.Name.ShouldBe("minimal.package");
            caller.Position.ShouldBe(caller.Length);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_ExhaustedReadKeepsPhysicalFailureRatherThanReparsing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-exhausted-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, MinimalManifestJson);
            using FileStream caller = File.OpenRead(path);
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
            primitives.UseSystemFiles();
            IOException failure = new IOException("required manifest read");
            primitives.Set<int>("ReadBorrowed", call => ScriptedFileSystemPrimitives.Fail<int>(failure, "read", call.Path));

            Should.Throw<IOException>(() => PackageManifest.Deserialize(caller, primitives.CreateContext(retries: 2)))
                .ShouldBeSameAs(failure);

            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("ReadBorrowed").ShouldBe(3);
            primitives.Count("SetPosition").ShouldBe(2);
            primitives.Waits.ShouldBe([10, 10]);
            caller.CanRead.ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Deserialize_PureAndProtectedPathsDoNotResolveEnvironment()
    {
        string path = Path.Combine(Path.GetTempPath(), $"manifest-context-{Guid.NewGuid():N}.json");
        string? original = Environment.GetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable);
        try
        {
            File.WriteAllText(path, MinimalManifestJson);
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
            primitives.UseSystemFiles();
            using Stream protectedStream = primitives.CreateContext().FileSystem.OpenProtectedRegularRead(
                path, TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, "invalid");

            PackageManifest.Deserialize(MinimalManifestJson).Name.ShouldBe("minimal.package");
            using MemoryStream memory = new MemoryStream(Encoding.UTF8.GetBytes(MinimalManifestJson));
            PackageManifest.Deserialize(memory).Name.ShouldBe("minimal.package");
            PackageManifest.Deserialize(protectedStream).Name.ShouldBe("minimal.package");
            Should.Throw<JsonException>(() => PackageManifest.Deserialize("malformed"));

            primitives.Count("PrepareBorrowedRead").ShouldBe(0);
            primitives.Waits.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, original);
            File.Delete(path);
        }
    }
}
