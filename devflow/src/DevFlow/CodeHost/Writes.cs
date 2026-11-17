namespace DevFlow.CodeHost;

/// <summary>
/// A person's access token at the code host, the one thing that authorises a write. A type of its
/// own rather than a string, so the app's credentials, an installation's token, which
/// <see cref="GitHub.InstallationTokens"/> makes as a string, cannot be passed where a write
/// wants one. Its constructor is internal, and <see cref="Link.UserTokens"/> is the one code that
/// calls it, from the person's stored token; C# cannot narrow that further within the assembly,
/// so <c>GitHubWriteTests.AUserTokenIsMadeOnlyByUserTokens</c> holds it. Prints nothing of the
/// token. See "Acting from the page" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed record UserAccessToken
{
    internal UserAccessToken(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>The token, which only the code host's request carries.</summary>
    public string Value { get; }

    /// <summary>Never the token.</summary>
    public override string ToString() => "UserAccessToken { }";
}

/// <summary>
/// What merging a pull request answers. A closed set, as <see cref="CodeHostResult{T}"/> is, so a
/// <c>switch</c> over the five is complete.
/// </summary>
public abstract record MergeResult
{
    private MergeResult()
    {
    }

    /// <summary>The pull request is merged, with a merge commit, as the person or the app.
    /// <paramref name="Commit"/> is the merge commit's full id, or null when the host's answer did
    /// not give one that reads as a commit id.</summary>
    public sealed record Merged(string? Commit) : MergeResult;

    /// <summary>Its head is no longer the commit named, so nothing was merged.</summary>
    public sealed record HeadMoved : MergeResult;

    /// <summary>The host will not merge it as it is: closed, in conflict, or held by a rule of the
    /// repository. <paramref name="Message"/> is the host's own words, data, or null when it gave
    /// none.</summary>
    public sealed record NotMergeable(string? Message) : MergeResult;

    /// <summary>The host refused the request for another reason, with its HTTP
    /// <paramref name="Status"/> and its own words, data, or null when it gave none: the token
    /// not taken, a permission the person lacks, a repository it does not show them.</summary>
    public sealed record Refused(int Status, string? Message) : MergeResult;

    /// <summary>The host did not answer, so whether it merged is not known.
    /// <paramref name="RetryAt"/> is when to try again, when it said.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : MergeResult;
}

/// <summary>What writing a comment answers. A closed set.</summary>
public abstract record CommentResult
{
    private CommentResult()
    {
    }

    /// <summary>The comment is posted, as the person.</summary>
    public sealed record Posted : CommentResult;

    /// <summary>The host refused it, with its HTTP <paramref name="Status"/> and its own words,
    /// data, or null when it gave none.</summary>
    public sealed record Refused(int Status, string? Message) : CommentResult;

    /// <summary>The host did not answer, so whether it was posted is not known.</summary>
    public sealed record Unavailable(DateTimeOffset? RetryAt) : CommentResult;
}