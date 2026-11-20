using System.Globalization;

namespace DevFlow.CodeHost;

/// <summary>
/// What a key the app makes for its own writes is for. Each purpose is narrowed, in the request
/// for the key, to exactly the permissions it needs on one repository, and none of them asks for
/// <c>workflows</c>, so no key Dev flow makes can change a project's CI. See "The program's own
/// writes" in devflow/docs/devflow-architecture.md.
/// </summary>
public enum WritePurpose
{
    /// <summary>Fetching and pushing code, handed to a worker: <c>contents: write</c> and
    /// <c>metadata: read</c>. It cannot comment or touch an issue or a label; it can push to any
    /// branch of its repository, and merge through the API, which GitHub files under contents
    /// too.</summary>
    Push,

    /// <summary>Opening a pull request or an issue, commenting and labelling:
    /// <c>issues: write</c>, <c>pull_requests: write</c> and <c>metadata: read</c>.</summary>
    Post,

    /// <summary>Merging a pull request only: <c>contents: write</c>, <c>pull_requests: read</c>
    /// and <c>metadata: read</c>.</summary>
    Merge,

    /// <summary>Fetching code only, handed to a worker for a review: <c>contents: read</c> and
    /// <c>metadata: read</c>. It writes nothing, so a reviewer's worker holds no key that could push;
    /// a key of the app's own rather than its read token, so it reaches one repository, is the
    /// worker's alone and runs out within the hour.</summary>
    Fetch,
}

/// <summary>
/// A key the app made to write to one repository for one purpose, the one thing that authorises
/// the program's own writes. A type of its own, so neither an installation's read token, a
/// string, nor a person's <see cref="UserAccessToken"/> can be passed where one is wanted, and the
/// compiler keeps the two write paths and the read path apart. Its constructor is internal, and
/// only each code host's issuer calls it, <see cref="GitHub.InstallationTokens"/> and
/// <see cref="FakeCodeHost"/>; <c>AppWriteTokenTests.OnlyTheIssuerMakesOne</c> holds that. Kept in
/// memory only, and prints nothing of the key.
/// </summary>
public sealed class AppWriteToken
{
    internal AppWriteToken(string value, Repository repository, WritePurpose purpose, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentNullException.ThrowIfNull(repository);
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a write purpose");
        }

        Value = value;
        Repository = repository;
        Purpose = purpose;
        ExpiresAt = expiresAt;
    }

    /// <summary>The key, which only the code host's request, or a worker's git, carries.</summary>
    public string Value { get; }

    /// <summary>The one repository the key reaches.</summary>
    public Repository Repository { get; }

    /// <summary>What the key was asked for.</summary>
    public WritePurpose Purpose { get; }

    /// <summary>When the code host stops taking it, an hour after it was made on GitHub.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Throws unless the key is for <paramref name="purpose"/> on <paramref name="repository"/>,
    /// by installation and id. A key for one write handed to another is a bug in Dev flow, so it
    /// is refused before the code host is asked, the same way in both code hosts.
    /// </summary>
    internal void Require(WritePurpose purpose, Repository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (Purpose != purpose)
        {
            throw new ArgumentException($"a key for {Purpose} cannot be used to {purpose}", nameof(purpose));
        }

        if (!Repository.IsSameAs(repository))
        {
            throw new ArgumentException("the key is for another repository", nameof(repository));
        }
    }

    /// <summary>Its purpose, repository and expiry, never the key.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"AppWriteToken {{ Purpose = {Purpose}, Repository = {Repository.Id}, ExpiresAt = {ExpiresAt:O} }}");
}

/// <summary>
/// What one of the app's writes answers: <see cref="Written"/> with what the host made, or one of
/// two failures. A closed set, as <see cref="CodeHostResult{T}"/> is, so a <c>switch</c> over the
/// three is complete. Nothing is retried in a code host; the caller decides.
/// </summary>
public abstract record WriteResult<T>
{
    private WriteResult()
    {
    }

    /// <summary>Done, and what the host answered for it.</summary>
    public sealed record Written(T Value) : WriteResult<T>;

    /// <summary>The host refused it, with its HTTP <paramref name="Status"/> and its own words,
    /// data, or null when it gave none: a key it does not take, a permission the key lacks, a
    /// repository, number or label it does not know, or a request it finds invalid.</summary>
    public sealed record Refused(int Status, string? Message) : WriteResult<T>;

    /// <summary>The host did not answer, or answered a success in a shape that does not parse, so
    /// whether it was done, or what it made, is not known.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : WriteResult<T>;
}
/// <summary>
/// What bringing one branch into another answers. A closed set, as <see cref="WriteResult{T}"/>
/// is, so a <c>switch</c> over the five is complete. Nothing is retried in a code host; the caller
/// decides.
/// </summary>
public abstract record BringInResult
{
    private BringInResult()
    {
    }

    /// <summary>Brought in, with a merge commit on the base. <paramref name="Commit"/> is the merge
    /// commit's full id, <paramref name="BroughtIn"/> the head's commit it merged, its second
    /// parent, and <paramref name="Parent"/> the base's commit it merged into, its first; each null
    /// when the host's answer did not give one that reads as a commit id.</summary>
    public sealed record Merged(string? Commit, string? BroughtIn, string? Parent = null) : BringInResult;

    /// <summary>The base holds the head already, so nothing was made.</summary>
    public sealed record NothingToBringIn : BringInResult;

    /// <summary>The two conflict, so nothing was made.</summary>
    public sealed record Conflict : BringInResult;

    /// <summary>The host refused it, with its HTTP <paramref name="Status"/> and its own words,
    /// data, or null when it gave none: a key it does not take, a base or head it does not know, or
    /// a rule of the repository.</summary>
    public sealed record Refused(int Status, string? Message) : BringInResult;

    /// <summary>The host did not answer, so whether it was made is not known.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : BringInResult;
}

/// <summary>
/// What making a branch answers. A closed set, as <see cref="WriteResult{T}"/> is, so a
/// <c>switch</c> over the four is complete. Nothing is retried in a code host; the caller decides.
/// </summary>
public abstract record BranchResult
{
    private BranchResult()
    {
    }

    /// <summary>Made, at <paramref name="Commit"/>, the full id of the commit the host made it
    /// at.</summary>
    public sealed record Made(string Commit) : BranchResult;

    /// <summary>The repository has a branch of that name already, so nothing was made.</summary>
    public sealed record Exists : BranchResult;

    /// <summary>The host refused it, with its HTTP <paramref name="Status"/> and its own words,
    /// data, or null when it gave none: a key it does not take, a branch to make it from that is
    /// not there, or a name it does not take.</summary>
    public sealed record Refused(int Status, string? Message) : BranchResult;

    /// <summary>The host did not answer, or answered a success in a shape that does not parse, so
    /// whether it was made is not known.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : BranchResult;
}