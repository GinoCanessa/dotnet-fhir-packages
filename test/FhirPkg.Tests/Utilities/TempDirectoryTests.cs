// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Utilities;

public class TempDirectoryTests : IDisposable
{
    private readonly List<string> _createdDirs = [];

    public void Dispose()
    {
        foreach (string dir in _createdDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Create_ReturnsDirectoryInSystemTemp()
    {
        string dir = TempDirectory.Create("test-pkg");
        _createdDirs.Add(dir);

        Directory.Exists(dir).ShouldBeTrue();
        dir.ShouldStartWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Create_ReturnsUniqueDirectories()
    {
        string dir1 = TempDirectory.Create("test-pkg");
        string dir2 = TempDirectory.Create("test-pkg");
        _createdDirs.Add(dir1);
        _createdDirs.Add(dir2);

        dir1.ShouldNotBe(dir2);
    }

    [Fact]
    public void Create_UsesPrefix()
    {
        string dir = TempDirectory.Create("my-prefix");
        _createdDirs.Add(dir);

        Path.GetFileName(dir).ShouldStartWith("my-prefix-");
    }

    [Fact]
    public void Create_WithFallbackRoot_StillPrefersSystemTemp()
    {
        string fallback = Path.Combine(Path.GetTempPath(), $"fallback-root-{Guid.NewGuid():N}");
        _createdDirs.Add(fallback);

        string dir = TempDirectory.Create("test-pkg", fallback);
        _createdDirs.Add(dir);

        // Should use system temp, not the fallback
        dir.ShouldStartWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        dir.ShouldNotContain(".temp");
    }

    [Fact]
    public void Create_NoFallbackDoesNotRestartSystemTempAfterExhaustion()
    {
        string tempRoot = NewRoot();
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(CurrentPlatform());
        primitives.UseSystemFiles();
        primitives.Set("GetTempPath", FileSystemReplayOutcome<string>.Complete(tempRoot));
        primitives.Set<bool>(
            "CreateDirectory",
            _ => ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "CreateDirectory"));
        FileSystemContext context = primitives.CreateContext(retries: 2);

        Should.Throw<IOException>(() => TempDirectory.Create("test-pkg", null, context));

        primitives.Count("GetTempPath").ShouldBe(1);
        primitives.Count("CreateDirectory").ShouldBe(3);
        primitives.Waits.Count.ShouldBe(2);
    }

    [Fact]
    public void Create_FallbackUsesDistinctTargetWithoutRebudgetingPrimary()
    {
        string tempRoot = NewRoot();
        string fallbackRoot = NewRoot();
        Directory.CreateDirectory(fallbackRoot);
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(CurrentPlatform());
        primitives.UseSystemFiles();
        primitives.Set("GetTempPath", FileSystemReplayOutcome<string>.Complete(tempRoot));
        primitives.Set<bool>(
            "CreateDirectory",
            call => call.Path!.Contains(".temp", StringComparison.Ordinal)
                ? SystemFileSystemPrimitives.Instance.CreateDirectory(call.Path!)
                : ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "CreateDirectory", call.Path));
        FileSystemContext context = primitives.CreateContext(retries: 2);

        string dir = TempDirectory.Create("test-pkg", fallbackRoot, context);
        _createdDirs.Add(dir);

        Directory.Exists(dir).ShouldBeTrue();
        dir.ShouldStartWith(Path.Combine(fallbackRoot, ".temp"));
        primitives.Count("GetTempPath").ShouldBe(1);
        primitives.Calls
            .Count(call => call.Operation == "CreateDirectory"
                && !call.Path!.Contains(".temp", StringComparison.Ordinal))
            .ShouldBe(3);
        primitives.Waits.Count.ShouldBe(2);
    }

    [Fact]
    public void Create_EmptySystemTempWithoutFallbackThrows()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(CurrentPlatform());
        primitives.Set("GetTempPath", FileSystemReplayOutcome<string>.Complete(string.Empty));
        FileSystemContext context = primitives.CreateContext();

        Should.Throw<IOException>(() => TempDirectory.Create("test-pkg", null, context));

        primitives.Count("CreateDirectory").ShouldBe(0);
    }

    [Fact]
    public void Create_InvalidInputIsNotAFallbackSignal()
    {
        string tempRoot = NewRoot();
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(CurrentPlatform());
        primitives.UseSystemFiles();
        primitives.Set("GetTempPath", FileSystemReplayOutcome<string>.Complete(tempRoot));
        primitives.Set<bool>(
            "CreateDirectory",
            _ => throw new ArgumentException("invalid"));
        FileSystemContext context = primitives.CreateContext();
        string fallbackRoot = NewRoot();

        Should.Throw<ArgumentException>(() => TempDirectory.Create("test-pkg", fallbackRoot, context));

        Directory.Exists(Path.Combine(fallbackRoot, ".temp")).ShouldBeFalse();
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tempdir-root-{Guid.NewGuid():N}");
        _createdDirs.Add(root);
        return root;
    }

    private static FileSystemPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? FileSystemPlatform.Windows
        : OperatingSystem.IsMacOS() ? FileSystemPlatform.MacOS
        : FileSystemPlatform.Linux;
}
