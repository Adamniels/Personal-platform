namespace DevFlow.Protocol;

/// <summary>
/// The machine API: where its routes are, and what the machine program sends and reads there, in
/// the JSON of <see cref="WorkerApi.Json"/>. The program calls <c>enroll</c> with a code and every
/// other route with its machine's key as <c>Authorization: Bearer dfm_…</c>. Here so the program,
/// which a later slice adds, and Dev flow share one definition of it, as the worker does. See
/// "Machines" in devflow/README.md.
/// </summary>
public static class MachineApi
{
    /// <summary>Where every machine route is.</summary>
    public const string Prefix = "/devflow/api/machine";

    /// <summary>A machine trades a code for its key, with an <see cref="EnrollRequest"/>, answered
    /// with an <see cref="EnrollAnswer"/>: the one machine route without a key.</summary>
    public const string EnrollPath = Prefix + "/enroll";

    /// <summary>A machine says what it is and runs, with a <see cref="MachineHelloRequest"/>,
    /// answered with a <see cref="MachineHelloAnswer"/>.</summary>
    public const string HelloPath = Prefix + "/hello";
}

/// <summary>
/// What the machine program sends to enrol. Each field is nullable so Dev flow can answer one that
/// is missing with <c>422</c> naming it, or, for the code, <c>401</c>, rather than a body it cannot
/// read.
/// </summary>
/// <param name="Code">The code "Add a machine" showed, <c>dfe_</c> and 22 characters.</param>
/// <param name="Name">The machine's name, such as its hostname, 1 to 255 characters.</param>
/// <param name="Os"><c>macos</c> or <c>linux</c>.</param>
/// <param name="Arch"><c>arm64</c> or <c>x64</c>.</param>
public sealed record EnrollRequest(string? Code, string? Name, string? Os, string? Arch);

/// <summary>What enrolling answers, the one time the key is ever sent.</summary>
/// <param name="MachineId">The machine's id.</param>
/// <param name="Key">The machine's key, <c>dfm_</c> and 43 characters, which the program keeps in
/// a file only its user can read.</param>
public sealed record EnrollAnswer(Guid MachineId, string Key);

/// <summary>What the machine program reports of itself, each time it says hello. Every field is
/// required, and nullable for the same reason as <see cref="EnrollRequest"/>'s.</summary>
/// <param name="Name">The machine's name, 1 to 255 characters.</param>
/// <param name="Os"><c>macos</c> or <c>linux</c>.</param>
/// <param name="Arch"><c>arm64</c> or <c>x64</c>.</param>
/// <param name="ProgramVersion">The program's version, its release, 1 to 255 characters.</param>
/// <param name="Docker"><c>running</c>, or why Docker is not, 1 to 255 characters.</param>
public sealed record MachineHelloRequest(string? Name, string? Os, string? Arch, string? ProgramVersion, string? Docker);

/// <summary>What <c>hello</c> answers.</summary>
/// <param name="Release">Dev flow's release, which the program compares with its own version to
/// update itself, in a later slice.</param>
public sealed record MachineHelloAnswer(string Release);