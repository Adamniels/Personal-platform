namespace DevFlow.Protocol;

/// <summary>
/// What a worker sends with <c>hello</c>. Every field is required; each is nullable so Dev flow
/// can answer one that is missing with <c>422</c> naming it, rather than a body it cannot read.
/// </summary>
/// <param name="WorkerId">The id the worker made when it started, a UUID that is not all
/// zeros.</param>
/// <param name="Name">Its name, the container's hostname.</param>
/// <param name="RecipeVersion">The version of its project's recipe it runs.</param>
/// <param name="BaseImage">The base image it runs on.</param>
public sealed record HelloRequest(Guid? WorkerId, string? Name, string? RecipeVersion, string? BaseImage);

/// <summary>What a worker sends when it asks for work.</summary>
/// <param name="WorkerId">The id it said hello with.</param>
public sealed record NextJobRequest(Guid? WorkerId);