// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using FhirPkg.IO;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Utilities;

public class IniParserTests
{
    private const string PackagesIniContent = """
        [packages]
        hl7.fhir.r4.core#4.0.1 = installed
        hl7.fhir.us.core#6.1.0 = installed

        [metadata]
        version = 1
        created = 2024-01-15
        """;

    [Fact]
    public void Parse_PackagesIni_AllSections()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(PackagesIniContent);

        result.Keys.ShouldContain("packages");
        result.Keys.ShouldContain("metadata");

        result["packages"].Keys.ShouldContain("hl7.fhir.r4.core#4.0.1");
        result["packages"]["hl7.fhir.r4.core#4.0.1"].ShouldBe("installed");

        result["metadata"]["version"].ShouldBe("1");
        result["metadata"]["created"].ShouldBe("2024-01-15");
    }

    [Fact]
    public void Parse_EmptyFile_ReturnsEmptySection()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse("");

        result.ShouldNotBeNull();
        // Should have at least the default empty-string section
        result.Keys.ShouldContain("");
    }

    [Fact]
    public void Parse_CommentsIgnored()
    {
        string content = """
            ; This is a comment
            # This is also a comment
            [section]
            key = value
            """;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(content);

        result["section"].Keys.ShouldContain("key");
        result["section"]["key"].ShouldBe("value");
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> original = IniParser.Parse(PackagesIniContent);
        string serialized = IniParser.Serialize(original);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> reparsed = IniParser.Parse(serialized);

        reparsed["packages"]["hl7.fhir.r4.core#4.0.1"].ShouldBe("installed");
        reparsed["metadata"]["version"].ShouldBe("1");
    }

    [Fact]
    public void Parse_KeyWithoutSection_PlacedInDefaultSection()
    {
        string content = """
            globalkey = globalvalue
            [section]
            key = value
            """;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(content);

        result.Keys.ShouldContain("");
        result[""]["globalkey"].ShouldBe("globalvalue");
    }

    [Fact]
    public void Parse_BareKey_HasEmptyValue()
    {
        string content = """
            [section]
            barekey
            """;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result = IniParser.Parse(content);

        result["section"].Keys.ShouldContain("barekey");
        result["section"]["barekey"].ShouldBe(string.Empty);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Func<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> act = () => IniParser.Parse(null!);

        Should.Throw<ArgumentNullException>(() => act());
    }

    [Fact]
    public void ParseFile_ExpectedMissingDoesNotWait()
    {
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Missing());
        FileSystemContext context = primitives.CreateContext();

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result =
            IniParser.ParseFile(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ini"), context);

        result.ShouldBeEmpty();
        primitives.Count("Probe").ShouldBe(1);
        primitives.Waits.ShouldBeEmpty();
        primitives.Count("OpenFileHandle").ShouldBe(0);
        primitives.Count("OpenOwnedDescriptor").ShouldBe(0);
    }

    [Fact]
    public async Task ParseFileAsync_ExpectedMissingDoesNotWait()
    {
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Missing());
        FileSystemContext context = primitives.CreateContext();

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> result =
            await IniParser.ParseFileAsync(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ini"),
                context,
                TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public void ParseFile_ProbeFaultIsNotAbsence()
    {
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        primitives.Set<FileSystemProbeResult>(
            "Probe",
            _ => ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(5, "Probe"));
        FileSystemContext context = primitives.CreateContext(retries: 2);

        Should.Throw<IOException>(() => IniParser.ParseFile("access-denied.ini", context));

        primitives.Count("Probe").ShouldBe(3);
        primitives.Waits.Count.ShouldBe(2);
    }

    [Fact]
    public void WriteFile_DirectOverwriteIsNotChangedToAtomicReplacement()
    {
        using TestDirectory directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "nested", "packages.ini");
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        primitives.UseSystemFiles();
        FileSystemContext context = primitives.CreateContext();
        Dictionary<string, IReadOnlyDictionary<string, string>> sections = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["packages"] = new Dictionary<string, string> { ["a#1"] = "installed" },
        };

        IniParser.WriteFile(path, sections, context);
        IniParser.WriteFile(path, sections, context);

        primitives.Count("MovePath").ShouldBe(0);
        Directory.GetFiles(Path.GetDirectoryName(path)!).Length.ShouldBe(1);
        File.ReadAllBytes(path).Take(3).ToArray().ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        IniParser.ParseFile(path, context)["packages"]["a#1"].ShouldBe("installed");
    }

    [Fact]
    public async Task WriteFileAsync_RoundTripsThroughContext()
    {
        using TestDirectory directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "round.ini");
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        primitives.UseSystemFiles();
        FileSystemContext context = primitives.CreateContext();
        Dictionary<string, IReadOnlyDictionary<string, string>> sections = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["s"] = new Dictionary<string, string> { ["k"] = "v" },
        };

        await IniParser.WriteFileAsync(path, sections, context, TestContext.Current.CancellationToken);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> parsed =
            await IniParser.ParseFileAsync(path, context, TestContext.Current.CancellationToken);

        parsed["s"]["k"].ShouldBe("v");
        primitives.Count("MovePath").ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFile_ReaderSharingPreservesDirectOverwrite(bool asynchronous)
    {
        using TestDirectory directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "shared.ini");
        File.WriteAllText(path, new string('x', 512));
        using SafeFileHandle retainedReader = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        int sharingChecks = 0;
        primitives.Enqueue<long>("GetHandleLength", call =>
        {
            RandomAccess.GetLength(retainedReader).ShouldBe(0);
            using SafeFileHandle concurrentReader = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            concurrentReader.IsInvalid.ShouldBeFalse();
            if (OperatingSystem.IsWindows())
            {
                IOException failure = Should.Throw<IOException>(() =>
                {
                    using SafeFileHandle competingWriter = File.OpenHandle(
                        path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                (failure.HResult & 0xFFFF).ShouldBe(32);
            }
            else
            {
                // Unix shared flock permits another writer; it is not replay proof.
                using SafeFileHandle competingWriter = File.OpenHandle(
                    path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                competingWriter.IsInvalid.ShouldBeFalse();
            }
            sharingChecks++;
            return SystemFileSystemPrimitives.Instance.GetLength(call.Handle!, call.Path!);
        });
        Dictionary<string, IReadOnlyDictionary<string, string>> sections = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["packages"] = new Dictionary<string, string> { ["a#1"] = "café ☕" },
        };
        FileSystemContext context = primitives.CreateContext();

        if (asynchronous)
            await IniParser.WriteFileAsync(path, sections, context, TestContext.Current.CancellationToken);
        else
            IniParser.WriteFile(path, sections, context);

        byte[] expected = Encoding.UTF8.GetBytes(
            $"[packages]{Environment.NewLine}a#1 = café ☕{Environment.NewLine}{Environment.NewLine}");
        byte[] retainedContent = new byte[expected.Length];
        RandomAccess.Read(retainedReader, retainedContent, 0).ShouldBe(expected.Length);
        retainedContent.ShouldBe(expected);
        RandomAccess.GetLength(retainedReader).ShouldBe(expected.Length);
        File.ReadAllBytes(path).ShouldBe(expected);
        sharingChecks.ShouldBe(1);
        string openOperation = OperatingSystem.IsWindows() ? "OpenFileHandle" : "OpenOwnedDescriptor";
        FileSystemPrimitiveCall opened = primitives.Calls.Single(call => call.Operation == openOperation);
        opened.Mode.ShouldBe(FileMode.Create);
        opened.Access.ShouldBe(FileAccess.Write);
        opened.Share.ShouldBe(FileShare.Read);
        opened.Options.ShouldBe(asynchronous ? FileOptions.Asynchronous : FileOptions.None);
        primitives.Calls.Where(call => call.Operation == "ConfigureFileSharing")
            .Select(call => call.Share).ShouldBe(OperatingSystem.IsWindows() ? [] : [FileShare.Read]);
        primitives.Count(asynchronous ? "WriteAtAsync" : "WriteAt").ShouldBe(1);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("MovePath").ShouldBe(0);
        Directory.GetFiles(directory.Path).ShouldBe([path]);
        primitives.Waits.ShouldBeEmpty();
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteFileAtomicallyAsync_UsesGenericDurabilityPort()
    {
        using TestDirectory directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "atomic.ini");
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        primitives.UseSystemFiles();
        FileSystemContext context = primitives.CreateContext();
        Dictionary<string, IReadOnlyDictionary<string, string>> sections = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["s"] = new Dictionary<string, string> { ["k"] = "v" },
        };

        await IniParser.WriteFileAtomicallyAsync(
            path, sections, context.DurableFiles, TestContext.Current.CancellationToken);

        IniParser.ParseFile(path, context)["s"]["k"].ShouldBe("v");
        Directory.GetFiles(directory.Path).Length.ShouldBe(1);
    }

    [Fact]
    public async Task ParseFileAsync_CancelledTokenThrowsBeforeIo()
    {
        ScriptedFileSystemPrimitives primitives = NewPrimitives();
        FileSystemContext context = primitives.CreateContext();
        using CancellationTokenSource source = new CancellationTokenSource();
        await source.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => IniParser.ParseFileAsync("cancelled.ini", context, source.Token));

        primitives.Calls.ShouldBeEmpty();
    }

    private static ScriptedFileSystemPrimitives NewPrimitives()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(
            OperatingSystem.IsWindows() ? FileSystemPlatform.Windows
            : OperatingSystem.IsMacOS() ? FileSystemPlatform.MacOS
            : FileSystemPlatform.Linux);
        // Scripted outcomes take priority; path resolution and real files fall through.
        primitives.UseSystemFiles();
        return primitives;
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ini-test-{Guid.NewGuid():N}");

        internal TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
