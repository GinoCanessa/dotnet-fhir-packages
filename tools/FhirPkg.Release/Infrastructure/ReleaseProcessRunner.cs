// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;

namespace FhirPkg.Release.Infrastructure;

internal interface IReleaseProcessRunner
{
    Task<ReleaseProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed class ReleaseProcessRunner : IReleaseProcessRunner
{
    public async Task<ReleaseProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new()
        {
            StartInfo = startInfo,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Unable to start '{fileName}'.");
        }

        Task<string> standardOutputTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask =
            process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            if (!process.HasExited)
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await Task.WhenAll(
                standardOutputTask,
                standardErrorTask).ConfigureAwait(false);
            throw;
        }

        string standardOutput =
            await standardOutputTask.ConfigureAwait(false);
        string standardError =
            await standardErrorTask.ConfigureAwait(false);
        return new ReleaseProcessResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    private static void KillProcess(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }
}

internal sealed record ReleaseProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
