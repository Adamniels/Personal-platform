using System.Net.Http.Headers;

using DevFlow.Settings;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DevFlow.CodeHost.GitHub;

/// <summary>How the GitHub code host is registered, by <see cref="DevFlowApp"/> in the GitHub mode
/// and by its tests, which then replace the client's handler and nothing else.</summary>
public static class GitHubServices
{
    public static IServiceCollection AddGitHubCodeHost(this IServiceCollection services, GitHubSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        // The clock decides when a token is too old and when a rate limit ends; a test's comes first.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(GitHubCodeHost.HttpClientName, client =>
            {
                client.BaseAddress = GitHubHttp.BaseAddress;
                client.Timeout = GitHubHttp.Timeout;
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(GitHubHttp.UserAgent, productVersion: null));
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", GitHubHttp.ApiVersion);
            })
            // Every header's value is left out of the client's logs, the tokens among them.
            .RedactLoggedHeaders(_ => true);
        services.AddSingleton(provider => new GitHubCodeHost(
            provider.GetRequiredService<IHttpClientFactory>(), settings, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICodeHost>(provider => provider.GetRequiredService<GitHubCodeHost>());
        return services;
    }
}