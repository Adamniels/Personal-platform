namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// The program's own writes, as the app, as GitHub documents them. Each takes an
/// <see cref="AppWriteToken"/>, a key <see cref="InstallationTokens.ForWrite"/> made for one
/// purpose on one repository, and refuses one for another purpose or repository before GitHub is
/// asked, so what each can do is what its key's permissions allow and no more. Nothing is
/// retried: the caller is a loop that decides. See "The program's own writes" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class GitHubCodeHost
{
    /// <summary>A new key from <see cref="InstallationTokens.ForWrite"/>, never kept.</summary>
    public Task<CodeHostResult<AppWriteToken>> ForWrite(Guid userId, Repository repository, WritePurpose purpose, CancellationToken cancellationToken) =>
        _tokens.ForWrite(repository, purpose, cancellationToken);
}