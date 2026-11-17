using System.Reflection;

using DevFlow.CodeHost;
using DevFlow.CodeHost.GitHub;

namespace DevFlow.Tests.GitHub;

/// <summary>
/// The program's reads cannot reach a key that writes. The reading loop and the runner's
/// conventions, everything under <c>Reading/</c> and <c>Runner/</c>, never name an
/// <see cref="AppWriteToken"/> or a <see cref="WritePurpose"/>, nor call anything that does; and no
/// read of the GitHub adapter reaches one, through every helper, lambda and async state machine it
/// runs. Read from the compiled code, so it holds for code that does not name the types in its
/// source too. See "The program's own writes" in devflow/docs/devflow-architecture.md.
/// </summary>
public sealed class ReadPathTests
{
    private static readonly HashSet<Type> WriteTypes = [typeof(AppWriteToken), typeof(WritePurpose)];

    private static readonly Assembly DevFlowAssembly = typeof(ICodeHost).Assembly;

    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The interface's operations that write or make a key that does: those taking a
    /// person's token, an app's key or a purpose. Every other one is a read.</summary>
    private static bool Writes(MethodInfo operation) =>
        operation.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(UserAccessToken) || WriteTypes.Contains(parameter.ParameterType));

    [Fact]
    public void TheReadingCodeCannotReachAWriteToken()
    {
        // Everything under Reading/ and Runner/, nested and generated types included.
        var reading = DevFlowAssembly.GetTypes()
            .Where(type => type.Namespace is { } name && (name == "DevFlow.Reading" || name.StartsWith("DevFlow.Reading.", StringComparison.Ordinal)
                || name == "DevFlow.Runner" || name.StartsWith("DevFlow.Runner.", StringComparison.Ordinal)))
            .ToList();
        Assert.Contains(reading, type => type.Namespace == "DevFlow.Reading");
        Assert.Contains(reading, type => type.Namespace == "DevFlow.Runner");
        var named = reading
            .SelectMany(type => type.GetMethods(Everything).Cast<MethodBase>().Concat(type.GetConstructors(Everything)))
            .Where(method => IlScan.References(method, WriteTypes))
            .Select(method => $"{method.DeclaringType}.{method.Name}")
            .Concat(reading.SelectMany(type => type.GetFields(Everything))
                .Where(field => field.FieldType == typeof(AppWriteToken) || field.FieldType == typeof(WritePurpose))
                .Select(field => $"{field.DeclaringType}.{field.Name}"));
        Assert.Empty(named);

        // And every read of the GitHub adapter, with all it runs.
        var map = typeof(GitHubCodeHost).GetInterfaceMap(typeof(ICodeHost));
        var reads = map.InterfaceMethods.Select((operation, i) => (operation, implementation: map.TargetMethods[i]))
            .Where(pair => !Writes(pair.operation))
            .Select(pair => (MethodBase)pair.implementation)
            .ToList();
        Assert.Contains(reads, read => read.Name == nameof(ICodeHost.OpenPullRequests));
        Assert.Contains(reads, read => read.Name == nameof(ICodeHost.File));
        var reached = IlScan.Reachable(DevFlowAssembly, reads);

        // The walk follows a read through its async state machine to the token it reads with, so
        // a path to a write key would be followed too.
        Assert.Contains(reached, method => IlScan.Outermost(method.DeclaringType!) == typeof(InstallationTokens));
        Assert.Empty(reached.Where(method => IlScan.References(method, WriteTypes)).Select(method => $"{method.DeclaringType}.{method.Name}"));
    }

    [Fact]
    public void TheScanFindsAWritePathWhereThereIsOne()
    {
        // The other side, so the test above is not passing because the scan sees nothing: from the
        // adapter's own ForWrite, the key is reached.
        var forWrite = typeof(GitHubCodeHost).GetMethod(nameof(ICodeHost.ForWrite))!;
        var reached = IlScan.Reachable(DevFlowAssembly, [forWrite]);

        Assert.Contains(reached, method => IlScan.Outermost(method.DeclaringType!) == typeof(InstallationTokens) && IlScan.References(method, WriteTypes));
    }
}