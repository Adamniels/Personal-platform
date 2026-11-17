using System.Text;

namespace DevFlow.Recipes;

/// <summary>
/// What a project's page needs to start a worker, and the two commands it shows: one saving the
/// key, shown once, right after it is made; and one fetching the project's recipe, building the
/// worker FROM the base image and starting it, shown always and never holding the key, which it
/// reads from the file the first saved. The owner pastes them in a terminal on the machine that
/// runs the workers; running the second again replaces the project's worker. See "Starting a
/// worker" in devflow/README.md.
/// </summary>
/// <param name="Image">The base image, <c>DEVFLOW_WORKER_IMAGE</c>.</param>
/// <param name="WorkerUrl">The address a worker calls Dev flow at from its container,
/// <c>DEVFLOW_WORKER_URL</c>.</param>
/// <param name="Origin">The platform's public origin, where the terminal fetches the recipe, since
/// the worker's address may only resolve inside a container: Docker Desktop's
/// <c>host.docker.internal</c> does not resolve on the Mac itself.</param>
public sealed record WorkerStart(string Image, Uri WorkerUrl, string Origin)
{
    /// <summary>The base image the hosted copy's compose file builds without a release's tag.</summary>
    public const string DefaultImage = "platform-hosted/devflow-worker:dev";

    /// <summary>Dev flow as a container on Docker Desktop reaches it on the dev stack: the host,
    /// on Dev flow's own port.</summary>
    public const string DefaultWorkerUrl = "http://host.docker.internal:8084";

    /// <summary>The most characters of a project's name in a command.</summary>
    private const int MostName = 40;

    /// <summary>
    /// The project's name in the commands, from its repository's: lowercase, every run of
    /// characters other than ASCII letters and digits one hyphen, no hyphen at either end, at most
    /// 40 characters; <c>project</c> when nothing is left. So it is a valid Docker image and
    /// container name, and a file name and a word the shell reads as it is.
    /// </summary>
    public static string ProjectName(string repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var name = new StringBuilder();
        foreach (var c in repository.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                name.Append(c);
            }
            else if (name.Length > 0 && name[^1] != '-')
            {
                name.Append('-');
            }
        }

        var text = name.ToString().TrimEnd('-');
        if (text.Length > MostName)
        {
            text = text[..MostName].TrimEnd('-');
        }

        return text.Length == 0 ? "project" : text;
    }

    /// <summary>The command that saves <paramref name="key"/> in <c>~/.devflow/&lt;project&gt;.env</c>,
    /// readable by the owner alone, in a subshell so the terminal's own umask is kept.</summary>
    public string SaveCommand(string repository, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return $"(umask 077 && mkdir -p ~/.devflow && printf 'DEVFLOW_PROJECT_KEY=%s\\n' '{key}' > ~/.devflow/{ProjectName(repository)}.env)";
    }

    /// <summary>
    /// The command that fetches the recipe with the key from the file, builds it FROM the base
    /// image with the version Dev flow read it at, from the response's header, and replaces the
    /// project's worker with one started from it, with 2 GB of memory. One block, its lines joined
    /// by <c>&amp;&amp;</c>, so a step that fails stops the rest.
    /// </summary>
    public string StartCommand(string repository)
    {
        var name = ProjectName(repository);
        var file = $"~/.devflow/{name}.env";
        var headers = $"/tmp/devflow-{name}.headers";
        var recipe = $"/tmp/devflow-{name}.Dockerfile";
        var url = WorkerUrl.GetLeftPart(UriPartial.Authority);
        return string.Join(" && \\\n  ",
            $"curl -fsS -H \"Authorization: Bearer $(sed -n 's/^DEVFLOW_PROJECT_KEY=//p' {file})\" -D {headers} -o {recipe} '{Origin}{RecipeEndpoints.Path}'",
            $"docker build --build-arg DEVFLOW_BASE={Image} --build-arg DEVFLOW_RECIPE_VERSION=\"$({VersionFrom(headers)})\" -t devflow-{name} - < {recipe}",
            $"{{ docker rm -f devflow-{name}-1 2>/dev/null; docker run -d --name devflow-{name}-1 --restart unless-stopped --memory 2g --env-file {file} -e DEVFLOW_URL={url} devflow-{name}; }}");
    }

    /// <summary>The shell that prints the recipe's version from the headers <c>curl -D</c> wrote
    /// to <paramref name="headers"/>, whatever the case of the header's name and without the
    /// carriage return each line ends in.</summary>
    public static string VersionFrom(string headers) =>
        $"awk 'tolower($1) == \"{RecipeEndpoints.VersionHeader.ToLowerInvariant()}:\" {{ print $2 }}' {headers} | tr -d '\\r'";
}
