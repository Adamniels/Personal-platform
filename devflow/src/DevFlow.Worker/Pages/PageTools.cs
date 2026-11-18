namespace DevFlow.Worker.Pages;

/// <summary>
/// What a build starts the project and takes its pictures with: the starter for the project's
/// <c>start</c>, as <c>agent</c> in a process group of its own; the handler the worker asks the
/// project's <c>url</c> with, which follows no redirect and uses no proxy, so an answer from a
/// login page counts as the project being up; where Chromium is, which the agent's environment,
/// built from nothing, is given; and the Playwright program, the image's pinned one on the agent's
/// <c>PATH</c>, never one <c>npx</c> would resolve from the project. See "Screenshots" in
/// devflow/README.md.
/// </summary>
internal sealed record PageTools(IBackgroundStarter Starter, HttpMessageHandler Probe, string BrowsersPath, string Playwright = "playwright")
{
    /// <summary>Where the base image keeps Playwright's Chromium, root's and readable by
    /// <c>agent</c>.</summary>
    public const string ImageBrowsers = "/ms-playwright";

    /// <summary>The image's tools, with the project run as <paramref name="runAs"/>.</summary>
    public static PageTools For(string? runAs, TimeProvider time) =>
        new(new SystemBackgroundStarter(runAs, time), new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) }, ImageBrowsers);
}