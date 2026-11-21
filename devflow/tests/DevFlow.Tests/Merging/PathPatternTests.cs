using DevFlow.CodeHost;
using DevFlow.Merging;

namespace DevFlow.Tests.Merging;

/// <summary>
/// The patterns of a project's list of paths, as <c>Microsoft.Extensions.FileSystemGlobbing</c>'s
/// <c>Matcher</c> reads them: <c>*</c> within a folder, <c>**</c> across folders, and a pattern
/// ending in <c>/</c> meaning everything under it, whatever the case. See "The merge check" in
/// devflow/README.md.
/// </summary>
public sealed class PathPatternTests
{
    public static TheoryData<string, string, bool> Cases() => new()
    {
        // Everything under a folder, at any depth, and nothing beside it.
        { "src/auth/**", "src/auth/login.rs", true },
        { "src/auth/**", "src/auth/tokens/key.rs", true },
        { "src/auth/**", "SRC/Auth/Login.rs", true },
        { "src/auth/**", "src/authz/policy.rs", false },
        { "src/auth/**", "lib/src/auth/login.rs", false },
        { "src/auth/**", "src/auth.rs", false },

        // A star stays within one folder: only the repository's own top level.
        { "*.sql", "schema.sql", true },
        { "*.sql", "SCHEMA.SQL", true },
        { "*.sql", "migrations/0001_init.sql", false },
        { "*.sql", "schema.sql.bak", false },
        { "**/*.sql", "migrations/0001_init.sql", true },
        { "**/*.sql", "schema.sql", true },

        // A folder, everything under it.
        { ".github/", ".github/workflows/ci.yml", true },
        { ".github/", ".github/CODEOWNERS", true },
        { ".github/", "docs/.github/notes.md", false },
        { ".github/", ".githubx/ci.yml", false },
        { ".github/", ".github", false },

        // One file, exactly.
        { "infra/hosted/compose.yaml", "infra/hosted/compose.yaml", true },
        { "infra/hosted/compose.yaml", "infra/hosted/compose.yaml.bak", false },
        { "infra/hosted/compose.yaml", "infra/compose.yaml", false },
        { "infra/hosted/compose.yaml", "other/infra/hosted/compose.yaml", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void PatternsMatchAsDocumented(string pattern, string path, bool matches) =>
        Assert.Equal(matches, PathPatterns.Matches(pattern, path));

    /// <summary>The first file, in the pull request's order, that a pattern matches, by its new or
    /// its old name, with the first pattern, in the list's order, that matches it.</summary>
    [Fact]
    public void TheFirstMatchIsNamed()
    {
        IReadOnlyList<string> patterns = ["*.sql", "src/auth/**"];
        Assert.Null(PathPatterns.FirstMatch(patterns, [new ChangedFile("web/list.tsx", null)]));
        Assert.Null(PathPatterns.FirstMatch([], [new ChangedFile("schema.sql", null)]));
        Assert.Equal(
            new PathMatch("src/auth/login.rs", "src/auth/**"),
            PathPatterns.FirstMatch(patterns, [new ChangedFile("web/list.tsx", null), new ChangedFile("src/auth/login.rs", null), new ChangedFile("schema.sql", null)]));
        Assert.Equal(
            new PathMatch("src/auth/old.rs", "src/auth/**"),
            PathPatterns.FirstMatch(patterns, [new ChangedFile("lib/old.rs", "src/auth/old.rs")]));
        Assert.Equal(
            new PathMatch("schema.sql", "*.sql"),
            PathPatterns.FirstMatch(["src/**", "*.sql"], [new ChangedFile("schema.sql", null)]));
    }

    /// <summary>What a pattern may be, the rules the settings route names under
    /// <c>wait_paths</c>: written, at most 200 characters, with no space at either end, no control
    /// character, no negation, no backslash, not from the root, and no <c>.</c> or <c>..</c>
    /// folder.</summary>
    [Theory]
    [InlineData("src/auth/**", true)]
    [InlineData("*.sql", true)]
    [InlineData(".github/", true)]
    [InlineData("docs/My Notes/*.md", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" src/**", false)]
    [InlineData("src/** ", false)]
    [InlineData("!src/**", false)]
    [InlineData("/src/**", false)]
    [InlineData("src\\auth\\**", false)]
    [InlineData("../secrets/**", false)]
    [InlineData("src/./auth/**", false)]
    [InlineData("src//auth", false)]
    [InlineData("src/\tauth", false)]
    public void APatternIsValidated(string pattern, bool valid) =>
        Assert.Equal(valid, PathPatterns.Problem(pattern) is null);

    [Fact]
    public void APatternIsAtMostTwoHundredCharacters()
    {
        Assert.Null(PathPatterns.Problem(new string('a', PathPatterns.Longest)));
        Assert.NotNull(PathPatterns.Problem(new string('a', PathPatterns.Longest + 1)));
        Assert.Equal(200, PathPatterns.Longest);
        Assert.Equal(100, PathPatterns.Most);
    }
}