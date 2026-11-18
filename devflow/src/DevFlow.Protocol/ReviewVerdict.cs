using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevFlow.Protocol;

/// <summary>What the reviewer says of the commit it read.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewVerdictKind>))]
public enum ReviewVerdictKind
{
    /// <summary>Nothing blocks it.</summary>
    [JsonStringEnumMemberName("approve")]
    Approve,

    /// <summary>At least one finding blocks it.</summary>
    [JsonStringEnumMemberName("changes")]
    Changes,
}

/// <summary>
/// The verdict a reviewer agent hands back, the fixed form Dev flow reads, as <see cref="Report"/>
/// is the developer's: every field required, lists may be empty. Its JSON Schema,
/// contracts/devflow/verdict.schema.json, is <see cref="Schema"/>, written from this type, and the
/// reviewer is given it; <see cref="TryRead"/> reads a verdict into this type with a missing,
/// unknown or mistyped field refused. It names no commit: the commit a verdict is for is the one
/// the worker checked out, never the agent's word. See "Review" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
/// <param name="Verdict"><c>approve</c>, or <c>changes</c> when a finding blocks it.</param>
/// <param name="Summary">What the reviewer found, in a few sentences.</param>
/// <param name="Findings">Each finding, blocking or not.</param>
/// <param name="ForOwner">Its view on each decision the developer marked for the owner.</param>
public sealed record ReviewVerdict(
    ReviewVerdictKind Verdict,
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<ReviewOwnerView> ForOwner)
{
    /// <summary>How a verdict is written and read, as a report is.</summary>
    public static JsonSerializerOptions Json => Form.Json;

    /// <summary>The verdict's JSON Schema, written from this type with <see cref="Json"/>: every
    /// object closed, every field required, and nothing nullable.</summary>
    public static JsonObject Schema() => Form.Schema<ReviewVerdict>("Dev flow's reviewer verdict");

    /// <summary>Its word, as the form writes it; not a field of the form.</summary>
    [JsonIgnore]
    public string Word => Verdict == ReviewVerdictKind.Approve ? "approve" : "changes";

    /// <summary>
    /// Reads <paramref name="verdict"/> as a verdict. False, with <paramref name="error"/> saying
    /// what is wrong, when it is not an object, a field is missing, unknown, null or of another type,
    /// <c>verdict</c> is neither word, a finding's <c>line</c> is below 0, <c>changes</c> has no
    /// blocking finding, or <c>approve</c> has one.
    /// </summary>
    public static bool TryRead(JsonElement verdict, [NotNullWhen(true)] out ReviewVerdict? read, [NotNullWhen(false)] out string? error)
    {
        read = null;
        if (!Form.TryRead<ReviewVerdict>(verdict, "verdict", out var parsed, out error))
        {
            return false;
        }

        if (parsed.Findings.Contains(null) || parsed.ForOwner.Contains(null))
        {
            error = $"{(parsed.Findings.Contains(null) ? "findings" : "for_owner")}: holds a null";
            return false;
        }

        if (parsed.Findings.Any(finding => finding.Line < 0))
        {
            error = "findings: a line is 0 or more, 0 when the finding is about no one line";
            return false;
        }

        var blocking = parsed.Findings.Any(finding => finding.Blocking);
        switch (parsed.Verdict)
        {
            case ReviewVerdictKind.Changes when !blocking:
                error = "verdict: changes needs at least one blocking finding";
                return false;
            case ReviewVerdictKind.Approve when blocking:
                error = "verdict: approve cannot have a blocking finding";
                return false;
        }

        read = parsed;
        error = null;
        return true;
    }
}

/// <summary>One thing the reviewer found.</summary>
/// <param name="Blocking">Whether it must be fixed before the card merges, rather than a nit.</param>
/// <param name="File">The file it is in, as the diff names it; empty when it is about no one
/// file.</param>
/// <param name="Line">The line in that file, 0 when it is about no one line.</param>
/// <param name="What">What is wrong.</param>
/// <param name="Why">Why it matters.</param>
public sealed record ReviewFinding(bool Blocking, string File, int Line, string What, string Why);

/// <summary>The reviewer's view on a decision the developer marked for the owner.</summary>
/// <param name="Decision">The decision, as the report names it.</param>
/// <param name="View">Whether the reviewer agrees, and why.</param>
public sealed record ReviewOwnerView(string Decision, string View);