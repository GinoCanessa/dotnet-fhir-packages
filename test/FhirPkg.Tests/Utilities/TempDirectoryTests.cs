// Copyright (c) Gino Canessa. Licensed under the MIT License.

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
}
