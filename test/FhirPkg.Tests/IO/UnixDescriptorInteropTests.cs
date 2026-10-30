// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using FhirPkg.IO;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public sealed class UnixDescriptorInteropTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"unix-descriptor-{Guid.NewGuid():N}");

    public UnixDescriptorInteropTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Linux", "X64", "Standard")]
    [InlineData("Linux", "Arm64", "Standard")]
    [InlineData("Linux", "X86", "Standard")]
    [InlineData("Linux", "Arm", "Standard")]
    [InlineData("MacOS", "X64", "Standard")]
    [InlineData("MacOS", "Arm64", "DarwinArm64")]
    [InlineData("MacOS", "X86", null)]
    [InlineData("MacOS", "Arm", null)]
    [InlineData("Windows", "X64", null)]
    [InlineData("Windows", "Arm64", null)]
    [InlineData("Unsupported", "Arm64", null)]
    public void Dispatch_UsesPlatformAndProcessArchitecture(string platform, string architecture, string? expected)
    {
        FileSystemPlatform selectedPlatform = Enum.Parse<FileSystemPlatform>(platform);
        Architecture processArchitecture = Enum.Parse<Architecture>(architecture);
        // Selection alone never executes an import for a foreign ABI.
        if (expected is null)
        {
            Should.Throw<PlatformNotSupportedException>(() =>
                UnixDescriptorInterop.SelectAbi(selectedPlatform, processArchitecture));
        }
        else
        {
            UnixDescriptorInterop.SelectAbi(selectedPlatform, processArchitecture).ToString().ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData("Linux", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0x800C2)]
    [InlineData("Linux", FileMode.Create, FileAccess.Write, FileShare.Inheritable, FileOptions.WriteThrough, 0x101041)]
    [InlineData("Linux", FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan, 0x80040)]
    [InlineData("Linux", FileMode.CreateNew, FileAccess.Write, FileShare.Inheritable, FileOptions.None, 0xC1)]
    [InlineData("Linux", FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0x80042)]
    [InlineData("Linux", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Inheritable, FileOptions.WriteThrough, 0x101042)]
    [InlineData("MacOS", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0x1000A02)]
    [InlineData("MacOS", FileMode.Create, FileAccess.Write, FileShare.Inheritable, FileOptions.WriteThrough, 0x281)]
    [InlineData("MacOS", FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan, 0x1000200)]
    [InlineData("MacOS", FileMode.CreateNew, FileAccess.Write, FileShare.Inheritable, FileOptions.None, 0xA01)]
    [InlineData("MacOS", FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.None, 0x1000202)]
    [InlineData("MacOS", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Inheritable, FileOptions.WriteThrough, 0x282)]
    public void OwnedOpenFlags_PreserveCreationInheritanceAndWriteThroughWithoutTruncation(
        string platform, FileMode mode, FileAccess access, FileShare share, FileOptions options, int expected)
    {
        NativeFileSystemPrimitives.OwnedOpenFlags(
            mode, access, share, options, Enum.Parse<FileSystemPlatform>(platform)).ShouldBe(expected);
    }

    public static IEnumerable<object[]> OwnedOpenFailures()
    {
        int[] windowsRejections = [2, 3, 5, 17, 32, 33, 80, 87, 123, 145, 183, 206, 267];
        foreach (int error in windowsRejections)
        {
            Type exceptionType = error switch
            {
                2 => typeof(FileNotFoundException),
                3 => typeof(DirectoryNotFoundException),
                5 => typeof(UnauthorizedAccessException),
                206 => typeof(PathTooLongException),
                _ => typeof(IOException),
            };
            yield return ["Windows", FileMode.Create, error, true, false, exceptionType];
        }

        foreach (string platform in new[] { "Linux", "MacOS" })
        {
            int[] unixRejections = [1, 2, 9, 13, 17, 18, 20, 21, 22, 28, 30, 36, 39, 40, 45, 62, 63, 66, 95];
            foreach (int error in unixRejections)
            {
                Type exceptionType = error switch
                {
                    1 or 13 => typeof(UnauthorizedAccessException),
                    2 => typeof(FileNotFoundException),
                    20 => typeof(DirectoryNotFoundException),
                    36 or 63 => typeof(PathTooLongException),
                    _ => typeof(IOException),
                };
                yield return [platform, FileMode.Create, error, true, false, exceptionType];
            }

            // EIO (5), EINTR (4), and unknown outcomes cannot establish ownership.
            foreach (int error in new[] { 0, 3, 4, 5, 11, 123456 })
                yield return [platform, FileMode.CreateNew, error, false, false, typeof(IOException)];
            yield return [platform, FileMode.CreateNew, 17, false, true, typeof(IOException)];
            yield return [platform, FileMode.CreateNew, 13, true, false, typeof(UnauthorizedAccessException)];
        }

        foreach (int error in new[] { 0, 4, 6, 13, 1117, 123456 })
            yield return ["Windows", FileMode.CreateNew, error, false, false, typeof(IOException)];
        yield return ["Windows", FileMode.CreateNew, 5, true, false, typeof(UnauthorizedAccessException)];
        yield return ["Windows", FileMode.CreateNew, 17, true, false, typeof(IOException)];
        yield return ["Windows", FileMode.CreateNew, 80, false, true, typeof(IOException)];
        yield return ["Windows", FileMode.CreateNew, 183, false, true, typeof(IOException)];
    }

    [Theory]
    [MemberData(nameof(OwnedOpenFailures))]
    public void OwnedOpenFailure_MapsActualPlatformSpecificResult(
        string platform, FileMode mode, int error, bool safe, bool collision, Type exceptionType)
    {
        string path = Path.Combine(_root, "owned-file");
        const long flags = 0x81000082;
        FileSystemReplayOutcome<int> outcome = NativeFileSystemPrimitives.OwnedOpenFailure<int>(
            path, mode, error, flags, Enum.Parse<FileSystemPlatform>(platform));

        outcome.Kind.ShouldBe(collision
            ? FileSystemReplayOutcomeKind.SemanticFailure
            : FileSystemReplayOutcomeKind.PrimitiveFailure);
        Exception exception;
        if (collision)
        {
            // Collisions remain semantic, with no replay token or minted primitive evidence.
            outcome.PrimitiveFailure.ShouldBeNull();
            exception = outcome.SemanticException!.SourceException;
            FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
        }
        else
        {
            outcome.SemanticException.ShouldBeNull();
            FileSystemPrimitiveFailure failure = outcome.PrimitiveFailure!;
            failure.ReplaySafety.ShouldBe(safe ? FileSystemReplaySafety.Safe : FileSystemReplaySafety.Unsafe);
            exception = failure.Exception;
            FileSystemFailureContext.TryGetOrigin(exception, out FileSystemFailureContext? context).ShouldBeTrue();
            context!.Operation.ShouldBe(platform == "Windows" ? "CreateFileW" : "open(owned file)");
            context.Path.ShouldBe(path);
            context.DestinationPath.ShouldBeNull();
            context.NativeErrorCode.ShouldBe(error);
            context.NativeFlags.ShouldBe(flags);
            context.IsTerminal.ShouldBeFalse();
        }
        exception.GetType().ShouldBe(exceptionType);
        exception.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(error);
        exception.Message.ShouldBe($"The file '{path}' could not be opened: {new Win32Exception(error).Message}");
        if (exception is FileNotFoundException missing)
            missing.FileName.ShouldBe(path);
    }

    [Theory]
    [InlineData("open", false)]
    [InlineData("open", true)]
    [InlineData("duplicate", false)]
    [InlineData("duplicate", true)]
    [InlineData("full-sync", false)]
    [InlineData("full-sync", true)]
    [InlineData("standard-sync", false)]
    [InlineData("standard-sync", true)]
    public void InjectedSeams_CaptureOnceAndUseLiveClassification(string operation, bool succeeds)
    {
        List<string> calls = [];
        int Invoke()
        {
            calls.Add("native");
            return succeeds ? 0 : -1;
        }
        int Error()
        {
            calls.ShouldBe(["native"]);
            calls.Add("errno");
            return 123456;
        }
        string path = Path.Combine(_root, "injected");
        FileSystemPrimitiveFailure? failure;
        FileSystemReplayOutcomeKind kind;
        string expectedOperation;
        long? expectedFlags;
        if (operation is "open" or "duplicate")
        {
            FileSystemReplayOutcome<int> outcome = operation == "open"
                ? NativeFileSystemPrimitives.OpenDescriptor(
                    path, 0x55, 0x1B6, (value, flags, mode) =>
                    {
                        value.ShouldBe(path);
                        flags.ShouldBe(0x55);
                        mode.ShouldBe(0x1B6U);
                        return Invoke();
                    }, Error)
                : NativeFileSystemPrimitives.DuplicateDescriptor(
                    7, 67, 64, (descriptor, command, minimum) =>
                    {
                        descriptor.ShouldBe(7);
                        command.ShouldBe(67);
                        minimum.ShouldBe(64);
                        return Invoke();
                    }, Error, path);
            kind = outcome.Kind;
            failure = outcome.PrimitiveFailure;
            expectedOperation = operation == "open" ? "open" : "fcntl(F_DUPFD_CLOEXEC)";
            expectedFlags = operation == "open" ? 0x55 : 67;
            if (succeeds)
                outcome.Value.ShouldBe(0);
        }
        else
        {
            FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> outcome = operation == "full-sync"
                ? NativeFileSystemPrimitives.SynchronizeMacFull(Invoke, Error, path)
                : NativeFileSystemPrimitives.SynchronizeStandard(Invoke, Error, macOS: false, path);
            kind = outcome.Kind;
            failure = outcome.PrimitiveFailure;
            expectedOperation = operation == "full-sync" ? "fcntl(F_FULLFSYNC)" : "fsync";
            expectedFlags = operation == "full-sync" ? 51 : null;
            if (succeeds)
                outcome.Value.ShouldBe(operation == "full-sync"
                    ? FileSystemDirectorySyncOutcome.Full
                    : FileSystemDirectorySyncOutcome.Standard);
        }

        calls.ShouldBe(["native", "errno"]);
        kind.ShouldBe(succeeds ? FileSystemReplayOutcomeKind.Completed : FileSystemReplayOutcomeKind.PrimitiveFailure);
        if (succeeds)
        {
            failure.ShouldBeNull();
        }
        else
        {
            failure!.ReplaySafety.ShouldBe(FileSystemReplaySafety.Safe);
            failure.Exception.ShouldBeOfType<IOException>().InnerException.ShouldBeOfType<Win32Exception>()
                .NativeErrorCode.ShouldBe(123456);
            FileSystemFailureContext.TryGetOrigin(failure.Exception, out FileSystemFailureContext? context).ShouldBeTrue();
            context!.Operation.ShouldBe(expectedOperation);
            context.Path.ShouldBe(path);
            context.NativeErrorCode.ShouldBe(123456);
            context.NativeFlags.ShouldBe(expectedFlags);
        }
    }

    [Fact]
    public void RealUnix_ModeOpenAndNoModeOpenPreservePermissions()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Physical Unix descriptor checks require Linux or macOS.");

        WriteHost();
        string control = Path.Combine(_root, "control");
        string path = Path.Combine(_root, "created");
        File.WriteAllBytes(control, []);
        int flags = NativeFileSystemPrimitives.OwnedOpenFlags(
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.None, NativeFileSystemPrimitives.Platform);
        UnixDescriptorResult created = UnixDescriptorInterop.OpenWithMode(path, flags, 0x1B6);
        created.ReturnValue.ShouldBeGreaterThanOrEqualTo(0, $"errno={created.Error}");
        try
        {
            UnixDescriptorInterop.Control(created.ReturnValue, 1).ReturnValue.ShouldBe(1); // F_GETFD / FD_CLOEXEC
            ReadUnixMode(path).ShouldBe(ReadUnixMode(control));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Requested mode=0666; managed=0{Convert.ToString((int)ReadUnixMode(control), 8)}; " +
                $"native=0{Convert.ToString((int)ReadUnixMode(path), 8)}; errno={created.Error}");
        }
        finally
        {
            Close(created.ReturnValue);
        }

        UnixDescriptorResult opened = UnixDescriptorInterop.OpenExisting(path, 0);
        opened.ReturnValue.ShouldBeGreaterThanOrEqualTo(0, $"errno={opened.Error}");
        try
        {
            UnixDescriptorInterop.Control(opened.ReturnValue, 1).ReturnValue.ShouldBe(0);
        }
        finally
        {
            Close(opened.ReturnValue);
        }
    }

    [Fact]
    public void RealUnix_DuplicateUsesNontrivialMinimumAndCloseOnExec()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Physical Unix descriptor checks require Linux or macOS.");

        WriteHost();
        string path = Path.Combine(_root, "duplicate");
        File.WriteAllText(path, "retained original");
        UnixDescriptorResult opened = UnixDescriptorInterop.OpenExisting(path, 0);
        opened.ReturnValue.ShouldBeGreaterThanOrEqualTo(0, $"errno={opened.Error}");
        try
        {
            int command = OperatingSystem.IsLinux()
                ? NativeFileSystemPrimitives.LinuxDuplicateFileDescriptorCloseOnExec
                : NativeFileSystemPrimitives.DarwinDuplicateFileDescriptorCloseOnExec;
            // Stay above the process's current descriptors so a lost vararg
            // cannot pass merely because the next free descriptor exceeds 64.
            int minimum = Math.Max(64, opened.ReturnValue + 64);
            FileSystemReplayOutcome<int> duplicate = NativeFileSystemPrimitives.DuplicateDescriptor(
                opened.ReturnValue, command, minimum, path);
            duplicate.Kind.ShouldBe(FileSystemReplayOutcomeKind.Completed);
            int descriptor = duplicate.Value;
            try
            {
                descriptor.ShouldBeGreaterThanOrEqualTo(minimum);
                descriptor.ShouldNotBe(opened.ReturnValue);
                UnixDescriptorResult control = UnixDescriptorInterop.Control(descriptor, 1);
                control.ReturnValue.ShouldBe(1); // F_GETFD / FD_CLOEXEC
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Original={opened.ReturnValue}; duplicate={descriptor}; minimum={minimum}; FD_CLOEXEC={control.ReturnValue}");
            }
            finally
            {
                Close(descriptor);
            }
            UnixDescriptorInterop.Control(opened.ReturnValue, 1).ReturnValue.ShouldBe(0);
        }
        finally
        {
            Close(opened.ReturnValue);
        }
    }

    [Fact]
    public void RealUnix_FailuresRetainImmediateErrnoAcrossLaterCalls()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Physical Unix descriptor checks require Linux or macOS.");

        WriteHost();
        string missing = Path.Combine(_root, "absent-parent", "file");
        int flags = NativeFileSystemPrimitives.OwnedOpenFlags(
            FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None, NativeFileSystemPrimitives.Platform);
        UnixDescriptorResult existing = UnixDescriptorInterop.OpenExisting(missing, 0);
        UnixDescriptorResult creating = UnixDescriptorInterop.OpenWithMode(missing, flags, 0x1B6);
        UnixDescriptorResult control = UnixDescriptorInterop.Control(-1, 1);
        int command = OperatingSystem.IsLinux()
            ? NativeFileSystemPrimitives.LinuxDuplicateFileDescriptorCloseOnExec
            : NativeFileSystemPrimitives.DarwinDuplicateFileDescriptorCloseOnExec;
        UnixDescriptorResult integer = UnixDescriptorInterop.ControlInteger(-1, command, 64);
        using SafeFileHandle invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        UnixDescriptorResult handle = UnixDescriptorInterop.ControlInteger(invalid, command, 64);
        Marshal.SetLastPInvokeError(123456);

        existing.ShouldBe(new UnixDescriptorResult(-1, 2));
        creating.ShouldBe(new UnixDescriptorResult(-1, 2));
        control.ShouldBe(new UnixDescriptorResult(-1, 9));
        integer.ShouldBe(new UnixDescriptorResult(-1, 9));
        handle.ShouldBe(new UnixDescriptorResult(-1, 9));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Captured errno: existing={existing.Error}; creating={creating.Error}; no-value={control.Error}; " +
            $"integer={integer.Error}; SafeFileHandle={handle.Error}; later errno={Marshal.GetLastPInvokeError()}");
    }

    [Theory]
    [InlineData(FileOptions.RandomAccess)]
    [InlineData(FileOptions.SequentialScan)]
    [InlineData(FileOptions.RandomAccess | FileOptions.SequentialScan)]
    public void RealMacOS_ReadAheadUsesProtectedHandle(FileOptions options)
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Physical F_RDAHEAD checks require macOS.");

        WriteHost();
        string path = Path.Combine(_root, "read-ahead");
        File.WriteAllText(path, "read-ahead payload");
        SafeFileHandle handle = File.OpenHandle(path);
        using (handle)
        {
            SystemFileSystemPrimitives.Instance.AdviseFileAccess(handle, options, path)
                .Kind.ShouldBe(FileSystemReplayOutcomeKind.Completed);
            RandomAccess.GetLength(handle).ShouldBe("read-ahead payload".Length);
            TestContext.Current.TestOutputHelper?.WriteLine($"F_RDAHEAD options={options}: PASS");
        }
        Should.Throw<ObjectDisposedException>(() => UnixDescriptorInterop.ControlInteger(handle, 45, 1));
    }

    private static UnixFileMode ReadUnixMode(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix permission checks require Linux or macOS.");
        return File.GetUnixFileMode(path);
    }

    private static void Close(int descriptor) =>
        NativeFileSystemPrimitives.CloseDescriptor(descriptor).Kind.ShouldBe(FileSystemReplayOutcomeKind.Completed);

    private static void WriteHost() =>
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"OS={RuntimeInformation.OSDescription}; ProcessArchitecture={RuntimeInformation.ProcessArchitecture}; " +
            $"OSArchitecture={RuntimeInformation.OSArchitecture}; Runtime={RuntimeInformation.FrameworkDescription}; " +
            $"TFM={AppContext.TargetFrameworkName}; " +
            $"ABI={UnixDescriptorInterop.SelectAbi(NativeFileSystemPrimitives.Platform, RuntimeInformation.ProcessArchitecture)}");
}
