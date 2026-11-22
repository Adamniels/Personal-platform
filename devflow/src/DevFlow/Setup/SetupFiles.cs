using System.Globalization;
using System.Text;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.Recipes;

namespace DevFlow.Setup;

/// <summary>What validating a commit's <c>.devflow/</c> came to: null when the code host did not
/// answer, so nothing is known.</summary>
/// <param name="Valid">Whether both files are there and valid.</param>
/// <param name="Problem">Why not, a sentence naming the file; null when valid.</param>
public sealed record SetupValidation(bool Valid, string? Problem);

/// <summary>
/// Whether a project's <c>.devflow/</c> is valid, the one rule the loop's read of the default branch
/// and the validation of what a setup run pushed share: both files there, the recipe by the recipe
/// route's rules, UTF-8 of at most 64 KB that copies no files, and <c>project.json</c> by its schema,
/// as <see cref="ProjectFile"/> reads it. See "Setting up a project" in devflow/README.md.
/// </summary>
public static class SetupFiles
{
    /// <summary>The command a setup run's validation is recorded under, in place of the project's
    /// own check, which a worker started from the base image cannot run.</summary>
    public const string Validation = "Dev flow's validation of .devflow/";

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Why <paramref name="recipe"/> and <paramref name="projectFile"/>, each null when
    /// missing, are not a valid <c>.devflow/</c>, or null when they are.</summary>
    public static string? Problem(byte[]? recipe, byte[]? projectFile)
    {
        if (recipe is null)
        {
            return $"{RecipeEndpoints.RecipePath} is missing.";
        }

        if (recipe.Length > RecipeEndpoints.MostBytes || Text(recipe) is not { } text)
        {
            return $"{RecipeEndpoints.RecipePath} is not UTF-8 text of at most 64 KB.";
        }

        if (Recipe.FirstRefused(text) is { } refused)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Line {refused.Line} of {RecipeEndpoints.RecipePath} is {refused.Instruction}, and a recipe cannot copy files into the image.");
        }

        if (projectFile is null)
        {
            return $"{Jobs.Dispatcher.ProjectFilePath} is missing.";
        }

        if (Text(projectFile) is not { } json)
        {
            return $"{Jobs.Dispatcher.ProjectFilePath} is not UTF-8 text.";
        }

        return ProjectFile.TryRead(json, out _, out var error)
            ? null
            : $"{Jobs.Dispatcher.ProjectFilePath} does not follow its schema: {error}";
    }

    /// <summary>
    /// Validates <c>.devflow/</c> as <paramref name="commit"/> has it, reading both files there as
    /// the app; null when the code host did not answer either, so the caller tries again.
    /// </summary>
    public static async Task<SetupValidation?> At(ICodeHost host, Guid userId, Repository repository, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (await Read(host, userId, repository, RecipeEndpoints.RecipePath, commit, cancellationToken) is not { } recipe
            || await Read(host, userId, repository, Jobs.Dispatcher.ProjectFilePath, commit, cancellationToken) is not { } projectFile)
        {
            return null;
        }

        var problem = Problem(recipe.Bytes, projectFile.Bytes);
        return new SetupValidation(problem is null, problem);
    }

    /// <summary>
    /// A setup run's result with Dev flow's validation of what it pushed as its check, so it is
    /// judged as a card's check that failed when not valid: a success that names a commit is
    /// validated there, and any other result carries no check, whatever its worker sent. Null when
    /// the code host did not answer, so the result is not taken now and its worker sends it again.
    /// </summary>
    public static async Task<Jobs.RunResult?> Validated(ICodeHost host, Guid userId, Repository repository, Jobs.RunResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || result.HeadCommit is not { } head)
        {
            return result with { CheckExitCode = null, CheckOutput = null };
        }

        if (await At(host, userId, repository, head, cancellationToken) is not { } validation)
        {
            return null;
        }

        var (exit, output) = AsCheck(validation);
        return result with { CheckExitCode = exit, CheckOutput = output };
    }

    /// <summary>The check result a setup's validation is judged as: exit 0 and a line saying both
    /// files are valid, or exit 1 and why not.</summary>
    public static (int ExitCode, string Output) AsCheck(SetupValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        return validation.Valid
            ? (0, ".devflow/Dockerfile and .devflow/project.json are valid.")
            : (1, validation.Problem!);
    }

    /// <summary>A file's bytes at the commit, null inside when it is not there; null when the code
    /// host did not answer.</summary>
    private static async Task<Found?> Read(ICodeHost host, Guid userId, Repository repository, string path, string commit, CancellationToken cancellationToken) =>
        await host.File(userId, repository, path, commit, cancellationToken) switch
        {
            CodeHostResult<byte[]>.Found { Value: var bytes } => new Found(bytes),
            CodeHostResult<byte[]>.NotFound => new Found(null),
            _ => null,
        };

    private static string? Text(byte[] bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private sealed record Found(byte[]? Bytes);
}