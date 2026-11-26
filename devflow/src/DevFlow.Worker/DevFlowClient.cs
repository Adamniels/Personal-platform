using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using DevFlow.Protocol;

namespace DevFlow.Worker;

/// <summary>What Dev flow answered a call, as the loop acts on it.</summary>
internal enum AnswerKind
{
    /// <summary><c>204</c>: done, or no work.</summary>
    Done,

    /// <summary><c>200</c> to <c>jobs/next</c>: a job, in <see cref="Answer.Body"/>.</summary>
    Job,

    /// <summary><c>200</c> to any other call: its answer, in <see cref="Answer.Body"/>.</summary>
    Answered,

    /// <summary><c>401</c>: the key is not one the project has.</summary>
    WrongKey,

    /// <summary><c>409</c> to <c>jobs/next</c>: say hello first.</summary>
    SayHelloFirst,

    /// <summary><c>422</c>: something the worker sent breaks a rule, in <see cref="Answer.Detail"/>.</summary>
    Invalid,

    /// <summary>No answer, a timeout or a <c>5xx</c>: Dev flow is away.</summary>
    Away,

    /// <summary>Any other answer, which this worker does not know.</summary>
    Unexpected,
}

/// <summary>An answer, with what the log says of it, and its body when it is a <c>200</c>: the job,
/// or a fresh push key.</summary>
internal sealed record Answer(AnswerKind Kind, string Detail, JsonElement Body = default);

/// <summary>
/// The worker's calls to Dev flow, over one <see cref="HttpClient"/> that carries the project's key
/// in its <c>Authorization</c> header. Never throws for Dev flow being away: every failure to reach
/// it is an <see cref="AnswerKind.Away"/>, so the loop decides what follows. Only stopping, through
/// the token, ends a call with an exception.
/// </summary>
internal sealed class DevFlowClient(HttpClient http)
{
    /// <summary>How long one call may take before Dev flow counts as away.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A client for Dev flow at the settings' address, with the key in its header, over
    /// <paramref name="handler"/> when a test gives one.</summary>
    public static HttpClient CreateHttpClient(WorkerSettings settings, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = settings.DevFlowUrl;
        client.Timeout = CallTimeout;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ProjectKey);
        return client;
    }

    public Task<Answer> Hello(HelloRequest request, CancellationToken stopping) =>
        Post(WorkerApi.HelloPath, request, stopping);

    public Task<Answer> NextJob(NextJobRequest request, CancellationToken stopping) =>
        Post(WorkerApi.NextJobPath, request, stopping);

    /// <summary>A fresh push key for the job, in the answer's body as a
    /// <see cref="PushKeyAnswer"/>.</summary>
    public Task<Answer> PushKey(Guid jobId, PushKeyRequest request, CancellationToken stopping) =>
        Post(WorkerApi.PushKeyPath(jobId), request, stopping);

    public Task<Answer> Progress(Guid jobId, ProgressRequest request, CancellationToken stopping) =>
        Post(WorkerApi.ProgressPath(jobId), request, stopping);

    /// <summary>The job's log lines and its heartbeat, answered with a <see cref="LogAnswer"/> in the
    /// answer's body.</summary>
    public Task<Answer> Log(Guid jobId, LogRequest request, CancellationToken stopping) =>
        Post(WorkerApi.LogPath(jobId), request, stopping);

    public Task<Answer> Result(Guid jobId, ResultRequest request, CancellationToken stopping) =>
        Post(WorkerApi.ResultPath(jobId), request, stopping);

    private async Task<Answer> Post<T>(string path, T body, CancellationToken stopping)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, WorkerApi.Json, stopping);
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent:
                    return new Answer(AnswerKind.Done, "204");
                case HttpStatusCode.OK:
                    using (var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(stopping), cancellationToken: stopping))
                    {
                        return new Answer(path == WorkerApi.NextJobPath ? AnswerKind.Job : AnswerKind.Answered, "200", answer.RootElement.Clone());
                    }

                case HttpStatusCode.Unauthorized:
                    return new Answer(AnswerKind.WrongKey, "401");
                case HttpStatusCode.Conflict:
                    return new Answer(AnswerKind.SayHelloFirst, "409");
                case HttpStatusCode.UnprocessableEntity:
                    return new Answer(AnswerKind.Invalid, $"422: {await Detail(response, stopping)}");
                case >= HttpStatusCode.InternalServerError:
                    return new Answer(AnswerKind.Away, $"Dev flow answered {status}");
                default:
                    return new Answer(AnswerKind.Unexpected, $"Dev flow answered {status}, which this worker does not know");
            }
        }
        catch (HttpRequestException e)
        {
            return new Answer(AnswerKind.Away, $"Dev flow could not be reached ({e.HttpRequestError})");
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return new Answer(AnswerKind.Away, $"Dev flow did not answer in time ({CallTimeout.TotalSeconds:0} seconds)");
        }
        catch (JsonException)
        {
            return new Answer(AnswerKind.Unexpected, "Dev flow answered something that is not JSON");
        }
    }

    /// <summary>The <c>detail</c> of a problem answer, which names what broke a rule; Dev flow
    /// writes it, and it never holds the key.</summary>
    private static async Task<string> Detail(HttpResponseMessage response, CancellationToken stopping)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(WorkerApi.Json, stopping);
            return problem?.Detail ?? "no detail";
        }
        catch (JsonException)
        {
            return "no detail";
        }
    }

    private sealed record Problem(string? Detail);
}