using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// The two commands "Add a machine" shows, <c>MachineInstall</c>: the install script fetched from
/// an address and run with that address and the code, once with the public origin, for any
/// machine, and once with <c>DEVFLOW_LOCAL_URL</c>, for the machine the platform runs on, which
/// cannot reach its own public address. See "Machines" in devflow/README.md.
/// </summary>
public sealed class MachineInstallTests
{
    [Fact]
    public async Task TheCommandHoldsTheCodeAndTheAddress()
    {
        await using var routes = await Routes.Start(settings: Machines.Settings(("DEVFLOW_LOCAL_URL", "http://127.0.0.1:9080")));
        var now = routes.Time.GetUtcNow();

        var response = await Machines.NewEnrollment(routes);

        // Shown once, and kept by no cache on the way.
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("no-store", response.Response.Headers.CacheControl.ToString());
        var answer = (await Machines.Json(response)).AsObject();
        Assert.Equal(["code", "expires_at", "command", "local_command"], answer.Select(pair => pair.Key));
        var code = (string)answer["code"]!;
        Assert.Equal($"{now.AddHours(1):yyyy-MM-ddTHH:mm:ss}+00:00", (string)answer["expires_at"]!);
        Assert.Equal(
            $"curl -fsSL 'https://platform.test/devflow/machine/install.sh' | sh -s -- 'https://platform.test' '{code}'",
            (string)answer["command"]!);
        Assert.Equal(
            $"curl -fsSL 'http://127.0.0.1:9080/devflow/machine/install.sh' | sh -s -- 'http://127.0.0.1:9080' '{code}'",
            (string)answer["local_command"]!);
    }

    [Fact]
    public async Task TheLocalAddressIsDevFlowsOwnUnlessSet()
    {
        await using var routes = await Routes.Start(settings: Machines.Settings(("DEVFLOW_PUBLIC_ORIGIN", "https://platform.example:8443")));

        var response = await routes.App.Send("POST", Machines.EnrollmentPath, Contract.Token, ("Origin", "https://platform.example:8443"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        var answer = await Machines.Json(response);
        var code = (string)answer["code"]!;
        Assert.Equal(
            $"curl -fsSL 'https://platform.example:8443/devflow/machine/install.sh' | sh -s -- 'https://platform.example:8443' '{code}'",
            (string)answer["command"]!);
        Assert.Equal(
            $"curl -fsSL 'http://127.0.0.1:8084/devflow/machine/install.sh' | sh -s -- 'http://127.0.0.1:8084' '{code}'",
            (string)answer["local_command"]!);
    }

    [Fact]
    public async Task AnotherOriginMakesNoCode()
    {
        await using var routes = await Routes.Start();

        var refused = await routes.App.Send("POST", Machines.EnrollmentPath, Contract.Token, ("Origin", "https://evil.example"));

        Assert.Equal(StatusCodes.Status403Forbidden, refused.Response.StatusCode);
        Assert.Empty(await Machines.Rows(routes));
    }
}