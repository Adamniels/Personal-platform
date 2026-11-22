using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using DevFlow.CodeHost;
using DevFlow.Protocol;
using DevFlow.PullRequests;
using DevFlow.Tests.PullRequests;
using DevFlow.Tests.Readings;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevFlow.Tests.Values;

using Jobs = DevFlow.Tests.Jobs.Jobs;

/// <summary>
/// Scrubbing a project's values: each known value, as itself, URL-encoded and in standard and
/// URL-safe base64, is replaced with <c>[value of NAME]</c> in everything a worker sends before Dev
/// flow stores it, and in everything Dev flow posts on GitHub before it posts it, with the one
/// function both use. See "Setting up a project" in devflow/README.md, and its accepted risk in
/// "Security decisions and accepted risks" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ScrubTests
{
    internal const string Name = "SERVICE_PASSWORD";

    /// <summary>A throwaway value whose URL encoding and base64 differ from it.</summary>
    internal const string Value = "p@ss w/rd+throwaway=1?";

    internal const string Placeholder = "[value of SERVICE_PASSWORD]";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string UrlEncoded => Uri.EscapeDataString(Value);

    internal static string Base64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(Value));

    internal static string Base64Url => Base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The value inside a longer encoding, as a Basic header carries it.</summary>
    internal static string Basic => Convert.ToBase64String(Encoding.UTF8.GetBytes($"ci-user:{Value}"));

    /// <summary>Every form that must never be found: the value, URL-encoded, both base64s without
    /// their padding, and the Basic header's.</summary>
    internal static IReadOnlyList<string> Forms => [Value, UrlEncoded, Base64.TrimEnd('='), Base64Url, Basic.TrimEnd('=')];

    /// <summary>Whether <paramref name="text"/> holds any form of the value.</summary>
    internal static bool Holds(string text) => Forms.Any(form => text.Contains(form, StringComparison.Ordinal));

    /// <summary>Every string in <paramref name="node"/>, property names among them, as read: the
    /// way to look for a form a JSON writer may have escaped.</summary>
    internal static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject properties => properties.SelectMany(pair => Strings(pair.Value).Prepend(pair.Key)),
        JsonArray items => items.SelectMany(Strings),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
        _ => [],
    };

    [Fact]
    public void EachFormIsFound()
    {
        var scrub = new ValueScrub([new JobValue(Name, Value), new JobValue("OTHER_KEY", "other-throwaway")]);

        // The value, URL-encoded and in both base64s, on its own, each replaced whole.
        foreach (var form in new[] { Value, UrlEncoded, Base64, Base64.TrimEnd('='), Base64Url })
        {
            Assert.Equal($"before {Placeholder} after", scrub.Scrub($"before {form} after"));
        }

        // Inside a Basic header, what is the value's own is replaced and what encodes the user
        // before it is kept; the lowercase escape and a form's space as a plus are found too.
        var header = scrub.Scrub($"Authorization: Basic {Basic}");
        Assert.False(Holds(header), header);
        Assert.StartsWith($"Authorization: Basic {Basic[..8]}", header, StringComparison.Ordinal);
        Assert.Contains(Placeholder, header, StringComparison.Ordinal);
        Assert.Equal(Placeholder, scrub.Scrub(System.Text.RegularExpressions.Regex.Replace(UrlEncoded, "%[0-9A-F]{2}", match => match.Value.ToLowerInvariant())));
        Assert.Equal(Placeholder, scrub.Scrub(System.Net.WebUtility.UrlEncode(Value)));
        Assert.Equal("[value of OTHER_KEY] and [value of SERVICE_PASSWORD]", scrub.Scrub($"other-throwaway and {Value}"));

        // In JSON, every string and every name, with every other value kept.
        var json = JsonSerializer.SerializeToElement(new JsonObject { [Value] = new JsonArray(Base64Url, 3, true, null), ["n"] = 4.5 });
        Assert.True(JsonNode.DeepEquals(
            new JsonObject { [Placeholder] = new JsonArray(Placeholder, 3, true, null), ["n"] = 4.5 },
            JsonNode.Parse(scrub.Scrub(json).GetRawText())));

        // A scrub that knows nothing changes nothing.
        Assert.Equal(Value, ValueScrub.None.Scrub(Value));
        Assert.Equal("ValueScrub { Forms = 0 }", ValueScrub.None.ToString());
        Assert.DoesNotContain(Value, scrub.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Value, new JobValue(Name, Value).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoValueIsStored()
    {
        // A build whose report, check output, Claude's usage and the worker's log, its standard
        // error, hold the value in each form; its review's verdict; and a fix that failed with it
        // in its reason.
        await using (var jobs = await Jobs.Start([Jobs.Card(1)]))
        {
            await Give(jobs);
            var report = Jobs.GoodReport();
            report["summary"] = $"Connected with {Value} to the test service.";
            report["handover"] = $"The address is https://ci-user:{UrlEncoded}@service.test.";
            var built = Built.Prepared(report);
            built["check"]!["output"] = $"connecting as {Value}\nTests: 12 passed";
            built["usage"]!["note"] = Base64Url;
            built["stderr"] = $"curl: sent Authorization: Basic {Basic}";
            await Built.Run(jobs, built);
            await Built.Review(jobs, Built.Verdict("changes", Built.Finding(what: $"The password {Base64} is printed in the log")));
            jobs.Routes.Time.Advance(TimeSpan.FromMinutes(1));
            var failed = await Built.Run(jobs, Built.PreparedFailure(inCard: false, $"git said: could not reach {Value}"));
            Assert.Equal("fix", (string)failed["kind"]!);

            await NothingHolds(jobs);
            var runs = string.Join('\n', (await jobs.Runs()).Select(run => $"{run.Report} {run.Reason} {run.CheckOutput} {run.Usage} {run.Stderr}"));
            Assert.Contains($"Connected with {Placeholder}", runs, StringComparison.Ordinal);
            Assert.Contains($"connecting as {Placeholder}", runs, StringComparison.Ordinal);
            Assert.Contains($"could not reach {Placeholder}", runs, StringComparison.Ordinal);
        }

        // A reading.
        await using (var jobs = await Jobs.Start([Jobs.Card(1)], read: false))
        {
            await Give(jobs);
            var reading = ReadJobTests.Reading(ReadJobTests.Ready(1));
            reading["touches"] = $"It reads the service's password, {Value}, from its settings.";
            await ReadJobTests.Read(jobs, ReadJobTests.Prepared(reading));
            await NothingHolds(jobs);
        }

        // A check of the setup's version, whose output printed it.
        await using (var jobs = await Jobs.Start([Jobs.Card(1)], checkedRecipe: false))
        {
            await Give(jobs);
            var job = await jobs.Job();
            Assert.Equal(Job.RecipeCheck, (string)job["kind"]!);
            var result = Jobs.Failure(Jobs.FirstWorker, "main", inCard: false, "unused");
            result["outcome"] = "succeeded";
            result["failure"] = null;
            result["check"] = new JsonObject { ["exit_code"] = 1, ["output"] = $"FAILED: could not log in as ci-user:{Value}" };
            Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
            await NothingHolds(jobs);
            await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
            Assert.Equal($"FAILED: could not log in as ci-user:{Placeholder}", await admin.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT output FROM recipe_checks WHERE project_id = @project", new { project = jobs.Project }, cancellationToken: Ct)));
        }
    }

    [Fact]
    public async Task NoValueIsPosted()
    {
        // A build's report stored before the value was given, as one is that a worker sent first:
        // its pull request, opened after, has the value scrubbed from its body.
        await using var jobs = await Jobs.Start([Jobs.Card(1)]);
        var job = await jobs.Job();
        var branch = (string)job["branch"]!;
        // The fake's git starts empty, so the default branch gets its first commit, as a worker
        // finds one there.
        var app = new Committer(FakeCodeHost.AppLogin, FakeCodeHost.AppEmail);
        Assert.NotNull(await jobs.Fake.CommitOnBranch(Jobs.User, Repo, "main", "main", "The first commit", app, Ct));
        var head = await jobs.Fake.CommitOnBranch(Jobs.User, Repo, branch, "main", "Build the thing", app, Ct);
        var report = Jobs.GoodReport();
        report["summary"] = $"Logged in with {Value}, and {Base64Url} in the fixture.";
        var result = Jobs.Result(Jobs.FirstWorker, branch, report);
        result["head_commit"] = head;
        Assert.Equal(StatusCodes.Status204NoContent, (await jobs.Post(WorkerApi.ResultPath((Guid)job["job_id"]!), result)).Response.StatusCode);
        Assert.NotNull(head);
        await Give(jobs);
        await jobs.Routes.App.Services.GetRequiredService<PullRequestOpener>().OpenPending(jobs.Project, Jobs.User, Ct);

        var opened = Assert.Single(Built.Opened(jobs));
        Assert.False(Holds(opened.Body!), opened.Body);
        Assert.Contains($"Logged in with {Placeholder}, and {Placeholder} in the fixture.", opened.Body, StringComparison.Ordinal);

        // A comment and an issue for the owner's attention, through the code host every poster is
        // given.
        var host = jobs.Routes.App.Services.GetRequiredService<ICodeHost>();
        var key = Assert.IsType<CodeHostResult<AppWriteToken>.Found>(await host.ForWrite(Jobs.User, Repo, WritePurpose.Post, Ct)).Value;
        Assert.IsType<WriteResult<Comment>.Written>(await host.CommentOnIssue(Jobs.User, key, Repo, opened.Number, $"The reviewer found {Base64} in the log.", Ct));
        Assert.IsType<WriteResult<Issue>.Written>(await host.OpenIssue(Jobs.User, key, Repo, $"Decide about {UrlEncoded}", $"Basic {Basic} was printed.", ["devflow-attention"], Ct));

        var posted = jobs.Fake.Writes(Jobs.User).Where(write => write.Kind is "comment" or "issue").ToList();
        Assert.Equal(2, posted.Count);
        Assert.All(posted, write => Assert.False(Holds($"{write.Title} {write.Body}"), $"{write.Title} {write.Body}"));
        Assert.Equal($"The reviewer found {Placeholder} in the log.", posted[0].Body);
        Assert.Contains(Placeholder, posted[1].Body, StringComparison.Ordinal);
        Assert.Equal($"Decide about {Placeholder}", posted[1].Title);
    }

    private static readonly DevFlow.CodeHost.Repository Repo = DevFlow.Tests.Jobs.FeatureJobs.Repo;

    private static async Task Give(Jobs jobs) =>
        Assert.Equal(StatusCodes.Status204NoContent, (await ProjectValuesTests.Give(jobs.Routes.App, jobs.Project, Name, Value)).Response.StatusCode);

    /// <summary>No row of any table holds any form of the value, read as the superuser past row
    /// level security.</summary>
    private static async Task NothingHolds(Jobs jobs)
    {
        await using var admin = await jobs.Routes.Database.OpenAdmin(Ct);
        var tables = await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
            cancellationToken: Ct));
        foreach (var table in tables)
        {
            foreach (var form in Forms)
            {
                // The JSON of a jsonb column escapes nothing in these forms but the quote, which
                // none holds, so the text of a row holds a form exactly when a string in it does.
                var found = await admin.QuerySingleAsync<long>(new CommandDefinition(
                    $"SELECT count(*) FROM \"{table}\" t WHERE strpos(t::text, @form) > 0",
                    new { form },
                    cancellationToken: Ct));
                Assert.True(found == 0, $"{table} holds {form}");
            }
        }
    }
}