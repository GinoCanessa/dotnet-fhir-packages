// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FhirPkg.Qualification;

internal sealed record ProcessQualificationResult(
    long SameIdentityWinnerBytes,
    long SameIdentityWaiterBytes,
    bool DifferentIdentityOverlap);

internal static class ProcessQualification
{
    private static readonly TimeSpan s_timeout =
        TimeSpan.FromSeconds(60);

    internal static async Task<ProcessQualificationResult> RunAsync(
        string hostPath,
        string workspace,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        string sameArchive = Path.Combine(
            workspace,
            "same.tgz");
        await File.WriteAllBytesAsync(
                sameArchive,
                DeterministicPackageArchive.Create(
                    "local.qualification.process.same",
                    "1.0.0",
                    "process-same"),
                cancellationToken)
            .ConfigureAwait(false);
        string sameCache = Path.Combine(cacheRoot, "same");
        string ownerBarrier = Path.Combine(
            workspace,
            "same-owner.ready");
        string ownerRelease = Path.Combine(
            workspace,
            "same-owner.release");
        string ownerResult = Path.Combine(
            workspace,
            "same-owner.result.json");
        string waiterResult = Path.Combine(
            workspace,
            "same-waiter.result.json");
        string ownerCounter = Path.Combine(
            workspace,
            "same-owner.counter");
        string waiterCounter = Path.Combine(
            workspace,
            "same-waiter.counter");
        string waiterContention = Path.Combine(
            workspace,
            "same-waiter.contention");
        using HostProcess owner = Start(
            hostPath,
            "manager-install",
            "--cache", sameCache,
            "--archive", sameArchive,
            "--name", "local.qualification.process.same",
            "--version", "1.0.0",
            "--barrier", ownerBarrier,
            "--release", ownerRelease,
            "--counter", ownerCounter,
            "--result", ownerResult);
        await WaitForFileAsync(
                ownerBarrier,
                cancellationToken)
            .ConfigureAwait(false);
        using HostProcess waiter = Start(
            hostPath,
            "manager-install",
            "--cache", sameCache,
            "--archive", sameArchive,
            "--name", "local.qualification.process.same",
            "--version", "1.0.0",
            "--counter", waiterCounter,
            "--contention", waiterContention,
            "--result", waiterResult);
        await WaitForFileAsync(
                waiterContention,
                cancellationToken)
            .ConfigureAwait(false);
        QualificationAssert.True(
            !waiter.HasExited,
            "Same-identity waiter exited after entering lock retry.");
        QualificationAssert.True(
            !File.Exists(waiterResult),
            "Same-identity waiter completed before the owner released its lease.");
        QualificationAssert.True(
            File.Exists(ownerBarrier)
                && !File.Exists(ownerRelease)
                && !owner.HasExited
                && !File.Exists(ownerResult),
            "Same-identity owner did not still hold its source barrier during waiter contention.");
        await File.WriteAllTextAsync(
                ownerRelease,
                "release",
                cancellationToken)
            .ConfigureAwait(false);
        HostExecution[] sameExecutions = await Task.WhenAll(
                owner.WaitAsync(cancellationToken),
                waiter.WaitAsync(cancellationToken))
            .ConfigureAwait(false);
        EnsureSuccess(sameExecutions[0], "same-identity owner");
        EnsureSuccess(sameExecutions[1], "same-identity waiter");
        HostResult ownerData = await ReadResultAsync(
                ownerResult,
                cancellationToken)
            .ConfigureAwait(false);
        HostResult waiterData = await ReadResultAsync(
                waiterResult,
                cancellationToken)
            .ConfigureAwait(false);

        string firstArchive = Path.Combine(
            workspace,
            "first.tgz");
        string secondArchive = Path.Combine(
            workspace,
            "second.tgz");
        await File.WriteAllBytesAsync(
                firstArchive,
                DeterministicPackageArchive.Create(
                    "local.qualification.process.first",
                    "1.0.0",
                    "process-first"),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(
                secondArchive,
                DeterministicPackageArchive.Create(
                    "local.qualification.process.second",
                    "1.0.0",
                    "process-second"),
                cancellationToken)
            .ConfigureAwait(false);
        string differentCache = Path.Combine(
            cacheRoot,
            "different");
        string firstBarrier = Path.Combine(
            workspace,
            "different-first.ready");
        string secondBarrier = Path.Combine(
            workspace,
            "different-second.ready");
        string differentRelease = Path.Combine(
            workspace,
            "different.release");
        using HostProcess first = Start(
            hostPath,
            "manager-install",
            "--cache", differentCache,
            "--archive", firstArchive,
            "--name", "local.qualification.process.first",
            "--version", "1.0.0",
            "--barrier", firstBarrier,
            "--release", differentRelease);
        using HostProcess second = Start(
            hostPath,
            "manager-install",
            "--cache", differentCache,
            "--archive", secondArchive,
            "--name", "local.qualification.process.second",
            "--version", "1.0.0",
            "--barrier", secondBarrier,
            "--release", differentRelease);
        await Task.WhenAll(
                WaitForFileAsync(
                    firstBarrier,
                    cancellationToken),
                WaitForFileAsync(
                    secondBarrier,
                    cancellationToken))
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                differentRelease,
                "release",
                cancellationToken)
            .ConfigureAwait(false);
        HostExecution[] differentExecutions = await Task.WhenAll(
                first.WaitAsync(cancellationToken),
                second.WaitAsync(cancellationToken))
            .ConfigureAwait(false);
        EnsureSuccess(
            differentExecutions[0],
            "different-identity first");
        EnsureSuccess(
            differentExecutions[1],
            "different-identity second");

        return new ProcessQualificationResult(
            ownerData.BytesRead,
            waiterData.BytesRead,
            DifferentIdentityOverlap: true);
    }

    private static HostProcess Start(
        string hostPath,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(hostPath);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process = new()
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The qualification process host did not start.");
        }

        return new HostProcess(process);
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(s_timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(20),
                    timeoutSource.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task<HostResult> ReadResultAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<HostResult>(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The process host result was empty.");
    }

    private static void EnsureSuccess(
        HostExecution execution,
        string description)
    {
        if (execution.ExitCode == 0)
            return;

        throw new QualificationInvariantException(
            $"The {description} process failed with exit code " +
            $"{execution.ExitCode.ToString(CultureInfo.InvariantCulture)}: " +
            execution.StandardError);
    }

    private sealed class HostProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;

        internal HostProcess(Process process)
        {
            _process = process;
            _standardOutput = process.StandardOutput.ReadToEndAsync();
            _standardError = process.StandardError.ReadToEndAsync();
        }

        internal bool HasExited => _process.HasExited;

        internal async Task<HostExecution> WaitAsync(
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeoutSource.CancelAfter(s_timeout);
            try
            {
                await _process.WaitForExitAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
                string[] output = await Task.WhenAll(
                        _standardOutput,
                        _standardError)
                    .ConfigureAwait(false);
                return new HostExecution(
                    _process.ExitCode,
                    output[0],
                    output[1]);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                TerminateIfRunning();
                throw new TimeoutException(
                    "The qualification process host timed out.");
            }
        }

        public void Dispose()
        {
            TerminateIfRunning();
            _process.Dispose();
        }

        private void TerminateIfRunning()
        {
            if (_process.HasExited)
                return;

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
                when (_process.HasExited)
            {
                return;
            }

            _process.WaitForExit();
        }
    }

    private sealed record HostExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record HostResult
    {
        public bool Success { get; init; }

        public long BytesRead { get; init; }
    }
}
