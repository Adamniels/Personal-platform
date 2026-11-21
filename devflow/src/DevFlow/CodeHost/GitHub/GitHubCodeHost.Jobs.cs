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

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/compare/{from}...{head}?per_page=1</c>, as the app: the files
    /// <paramref name="head"/> changed since the two parted, its first page holding every file
    /// GitHub lists, at most 300, and one commit, since the commits are not read. Each by its
    /// <c>filename</c>, a renamed one's new path, each once. Anything but two full commit ids is
    /// never sent.
    /// </summary>
    public async Task<CodeHostResult<IReadOnlyList<string>>> ChangedFiles(Guid userId, Repository repository, string from, string head, CancellationToken cancellationToken)
    {
        if (!FullCommit(from) || !FullCommit(head))
        {
            throw new ArgumentException("a comparison is of two full commit ids");
        }

        return Map(
            await One<CompareAnswer>(repository, $"{Repo(repository)}/compare/{from}...{head}?per_page=1", cancellationToken),
            answer => (IReadOnlyList<string>)[.. (answer.Files ?? []).Select(file => file.Filename is { Length: > 0 } name ? name : throw new JsonException("a file with no name"))
                .Distinct(StringComparer.Ordinal).Take(ICodeHost.MostChangedFiles)]);
    }

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/pulls/{number}/files?per_page=100</c>, as the app, paged to the
    /// end, at most 3000 files, GitHub's limit: each by its <c>filename</c>, and a renamed one's
    /// <c>previous_filename</c> beside it. A file with no name is an answer that does not parse,
    /// and a page that fails fails the whole, since a missing file could be the one that decides.
    /// </summary>
    public async Task<CodeHostResult<IReadOnlyList<ChangedFile>>> PullRequestFiles(Guid userId, Repository repository, int number, CancellationToken cancellationToken) =>
        Map(
            await List<IReadOnlyList<PullFileAnswer>, PullFileAnswer>(Invariant($"{Repo(repository)}/pulls/{number}/files?per_page=100"), page => page, AsApp(repository, cancellationToken)),
            files => (IReadOnlyList<ChangedFile>)[.. files.Select(file => new ChangedFile(
                file.Filename is { Length: > 0 } name ? name : throw new JsonException("a file with no name"),
                file.PreviousFilename is { Length: > 0 } previous ? previous : null))]);

    /// <summary>A full commit id, 40 or 64 lowercase hex digits.</summary>
    private static bool FullCommit(string? commit) =>
        commit is { Length: 40 or 64 } && commit.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private sealed record CompareAnswer(IReadOnlyList<CompareFile>? Files);

    private sealed record CompareFile(string? Filename);

    /// <summary>A file as GitHub lists it: <c>previous_filename</c> only on a rename.</summary>
    private sealed record PullFileAnswer(string? Filename, string? PreviousFilename = null);

    private sealed record RepositoryInfoAnswer(string DefaultBranch);

    private sealed record BotAnswer(string Login, long Id);
}