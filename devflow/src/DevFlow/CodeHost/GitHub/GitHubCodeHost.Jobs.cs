using System.Globalization;
using System.Text.Json;

namespace DevFlow.CodeHost.GitHub;

/// <summary>
/// What handing out a job reads, as the app, with the read only token every read has: the
/// repository's default branch, which the job's branch is made from, and the app's bot, which a
/// worker commits as. See "Jobs" in devflow/README.md.
/// </summary>
public sealed partial class GitHubCodeHost
{
    /// <summary>The app's bot once read; it never changes while the app is the same.</summary>
    private Committer? _committer;

    /// <summary><c>GET /repos/{owner}/{repo}</c>, of which <c>default_branch</c> is read; an empty
    /// one is an answer that does not parse.</summary>
    public async Task<CodeHostResult<string>> DefaultBranch(Guid userId, Repository repository, CancellationToken cancellationToken) =>
        Map(
            await One<RepositoryInfoAnswer>(repository, Repo(repository), cancellationToken),
            answer => answer.DefaultBranch is { Length: > 0 } branch ? branch : throw new JsonException("no default branch"));

    /// <summary>
    /// <c>GET /users/{slug}[bot]</c>, the app's bot, read once and kept: its login, and GitHub's
    /// no-reply address for it, <c>{id}+{login}@users.noreply.github.com</c>, which GitHub ties a
    /// commit to the bot by. An answer for another account than the one asked for is an answer that
    /// does not parse, and nothing is kept.
    /// </summary>
    public async Task<CodeHostResult<Committer>> AppCommitter(Guid userId, Repository repository, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _committer) is { } kept)
        {
            return new CodeHostResult<Committer>.Found(kept);
        }

        var bot = $"{_settings.AppSlug}[bot]";
        var read = Map(await One<BotAnswer>(repository, $"users/{Escape(bot)}", cancellationToken), answer =>
            string.Equals(answer.Login, bot, StringComparison.OrdinalIgnoreCase) && answer.Id > 0
                ? new Committer(answer.Login, string.Create(CultureInfo.InvariantCulture, $"{answer.Id}+{answer.Login}@users.noreply.github.com"))
                : throw new JsonException("an account that is not the app's bot"));
        if (read is CodeHostResult<Committer>.Found { Value: var committer })
        {
            Volatile.Write(ref _committer, committer);
        }

        return read;
    }

    private sealed record RepositoryInfoAnswer(string DefaultBranch);

    private sealed record BotAnswer(string Login, long Id);
}