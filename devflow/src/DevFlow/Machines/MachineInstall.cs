namespace DevFlow.Machines;

/// <summary>
/// The two commands "Add a machine" shows: the install script fetched from an address and run with
/// that address and the code, <c>curl -fsSL '&lt;address&gt;/devflow/machine/install.sh' | sh -s --
/// '&lt;address&gt;' '&lt;code&gt;'</c>. <see cref="Command"/> names the public origin, for any
/// machine; <see cref="LocalCommand"/> names <c>DEVFLOW_LOCAL_URL</c>, for the machine the platform
/// runs on, which cannot reach its own public address. Each part is in single quotes, and none can
/// hold one: the origin and the address are a scheme, a host and a port, as their settings parse
/// them, and a code is base64url. The script's route comes in a later slice, "Installing the
/// machine program". See "Machines" in devflow/README.md.
/// </summary>
/// <param name="publicOrigin"><c>DEVFLOW_PUBLIC_ORIGIN</c>.</param>
/// <param name="localUrl"><c>DEVFLOW_LOCAL_URL</c>.</param>
public sealed class MachineInstall(string publicOrigin, string localUrl)
{
    /// <summary>Where the install script is, under an address.</summary>
    public const string ScriptPath = "/devflow/machine/install.sh";

    /// <summary>The command for any machine, at the public origin.</summary>
    public string Command(string code) => For(publicOrigin, code);

    /// <summary>The command for the machine the platform runs on, at its local address.</summary>
    public string LocalCommand(string code) => For(localUrl, code);

    private static string For(string address, string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return $"curl -fsSL '{address}{ScriptPath}' | sh -s -- '{address}' '{code}'";
    }
}