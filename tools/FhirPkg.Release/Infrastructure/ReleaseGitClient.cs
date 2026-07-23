// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Release.Infrastructure;

internal interface IReleaseGitClient
{
    Task<ReleaseGitCommandResult> ResolveHeadCommitAsync(
        CancellationToken cancellationToken);

    Task<ReleaseGitCommandResult> FetchReleaseTagAsync(
        string tag,
        CancellationToken cancellationToken);

    Task<ReleaseGitCommandResult> ResolveTagCommitAsync(
        string tag,
        CancellationToken cancellationToken);

    Task<ReleaseGitCommandResult> FetchOriginMainAsync(
        CancellationToken cancellationToken);

    Task<ReleaseGitCommandResult> ResolveOriginMainCommitAsync(
        CancellationToken cancellationToken);

    Task<ReleaseGitCommandResult> CheckAncestorAsync(
        string ancestorCommit,
        string descendantRevision,
        CancellationToken cancellationToken);
}

internal sealed class ReleaseGitClient : IReleaseGitClient
{
    private readonly IReleaseProcessRunner _processRunner;

    internal ReleaseGitClient(IReleaseProcessRunner processRunner)
    {
        _processRunner =
            processRunner ??
            throw new ArgumentNullException(nameof(processRunner));
    }

    public Task<ReleaseGitCommandResult> ResolveHeadCommitAsync(
        CancellationToken cancellationToken) =>
        RunGitAsync(
            ["rev-parse", "HEAD"],
            cancellationToken);

    public Task<ReleaseGitCommandResult> FetchReleaseTagAsync(
        string tag,
        CancellationToken cancellationToken) =>
        RunGitAsync(
            [
                "fetch",
                "--force",
                "origin",
                $"refs/tags/{tag}:refs/tags/{tag}",
            ],
            cancellationToken);

    public Task<ReleaseGitCommandResult> ResolveTagCommitAsync(
        string tag,
        CancellationToken cancellationToken) =>
        RunGitAsync(
            ["rev-list", "-n", "1", tag],
            cancellationToken);

    public Task<ReleaseGitCommandResult> FetchOriginMainAsync(
        CancellationToken cancellationToken) =>
        RunGitAsync(
            [
                "fetch",
                "--no-tags",
                "origin",
                "+refs/heads/main:refs/remotes/origin/main",
            ],
            cancellationToken);

    public Task<ReleaseGitCommandResult> ResolveOriginMainCommitAsync(
        CancellationToken cancellationToken) =>
        RunGitAsync(
            ["rev-parse", "origin/main"],
            cancellationToken);

    public Task<ReleaseGitCommandResult> CheckAncestorAsync(
        string ancestorCommit,
        string descendantRevision,
        CancellationToken cancellationToken) =>
        RunGitAsync(
            [
                "merge-base",
                "--is-ancestor",
                ancestorCommit,
                descendantRevision,
            ],
            cancellationToken);

    private async Task<ReleaseGitCommandResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ReleaseProcessResult result =
            await _processRunner.RunAsync(
                "git",
                arguments,
                cancellationToken).ConfigureAwait(false);
        return new ReleaseGitCommandResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }
}

internal sealed record ReleaseGitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    internal string Output =>
        StandardOutput.Trim();
}
