using System.Net;

using DevFlow.Worker.Agents;
using DevFlow.Worker.Pages;

namespace DevFlow.Worker.Tests;

/// <summary>
/// Stands in for the project's start: records each program it is asked to start, runs what the test
/// says at that moment, and answers a process that ends when asked and runs nothing.
/// </summary>
internal sealed class FakePageStarter : IBackgroundStarter
{
    private readonly List<FakeProject> _started = [];

    /// <summary>What the test does when the project is started, given what was asked.</summary>
    public Func<ProcessSpec, Task>? OnStart { get; set; }

    /// <summary>The exit code the project answers, null while it runs.</summary>
    public int? ExitCode { get; set; }

    /// <summary>What the project wrote, each line handed to the spec's line sink as it starts, as
    /// a real program's would be.</summary>
    public string Output { get; set; } = "";

    public IReadOnlyList<FakeProject> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public async Task<IBackgroundProcess> Start(ProcessSpec spec, CancellationToken cancellationToken)
    {
        if (OnStart is not null)
        {
            await OnStart(spec);
        }

        if (spec.Lines is { } sink)
        {
            foreach (var line in Output.Split('\n')[..^(Output.EndsWith('\n') ? 1 : 0)])
            {
                sink(ProcessStream.Stdout, line);
            }
        }

        var project = new FakeProject(spec, this);
        lock (_started)
        {
            _started.Add(project);
        }

        return project;
    }
}

/// <summary>One project the fake starter started: what it was asked, and whether it was ended, and
/// when, by the count of pictures taken before.</summary>
internal sealed class FakeProject(ProcessSpec spec, FakePageStarter starter) : IBackgroundProcess
{
    public ProcessSpec Spec { get; } = spec;

    public int Ends { get; private set; }

    /// <summary>Whether the project's working copy was still there when it was ended.</summary>
    public bool CopyThereAtEnd { get; private set; }

    public int? ExitCode => starter.ExitCode;

    public string Output => starter.Output;

    public Task End()
    {
        Ends++;
        CopyThereAtEnd = Directory.Exists(Spec.WorkingDirectory);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Stands in for the project's url: answers each ask as the test says, 200 unless told
/// otherwise, and counts the asks.</summary>
internal sealed class FakeProbe : HttpMessageHandler
{
    private int _asks;

    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public int Asks => Volatile.Read(ref _asks);

    public List<Uri> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _asks);
        lock (Asked)
        {
            Asked.Add(request.RequestUri!);
        }

        return Task.FromResult(Answer(request));
    }
}

/// <summary>
/// A stand in for Playwright on the agent's PATH, a shell script: each run writes its arguments and
/// the browsers' folder it was given as one line to the bench's log, and copies
/// <see cref="Png"/> to the file named last; an address with <c>/broken</c> in it fails as a page
/// that does not load, and one with <c>/big</c> writes a file over 5 MB.
/// </summary>
internal static class Camera
{
    /// <summary>A PNG, one pixel.</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>What the stand in says when a page does not load.</summary>
    public const string Broken = "page.goto: net::ERR_CONNECTION_REFUSED";

    public static string LogOf(string root) => Path.Combine(root, "playwright.log");

    public static void Install(string bin, string root)
    {
        var picture = Path.Combine(root, "picture.png");
        File.WriteAllBytes(picture, Png);
        var script = Path.Combine(bin, "playwright");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            printf '%s browsers=%s\n' "$*" "$PLAYWRIGHT_BROWSERS_PATH" >> '{{LogOf(root)}}'
            for last; do :; done
            case "$*" in
              */broken*) echo '{{Broken}}' >&2; exit 1 ;;
              */big*) cp '{{picture}}' "$last" && head -c 6000000 /dev/zero >> "$last"; exit 0 ;;
            esac
            cp '{{picture}}' "$last"
            """);
        Build.Bench.Executable(script);
    }
}