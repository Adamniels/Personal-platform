using System.Buffers.Text;
using System.Reflection;
using System.Text.RegularExpressions;

using Dapper;

using DevFlow.Settings;
using DevFlow.Tests.GitHub;
using DevFlow.Tests.Reading;

using Microsoft.AspNetCore.Http;

namespace DevFlow.Tests.Machines;

/// <summary>
/// A machine's key: made when the machine enrols, answered that once, stored only as its SHA-256,
/// and, with the enrolment code, never compared with a stored value. See "Machines" in
/// devflow/README.md and the machine key's entries in "Security decisions and accepted risks" in
/// devflow/docs/devflow-architecture.md.
/// </summary>
public sealed partial class MachineKeyTests
{
    private static CancellationToken Ct => Machines.Ct;

    [GeneratedRegex("^dfm_[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyShape();

    [Fact]
    public async Task AKeyIsDfmAndStoredAsItsHash()
    {
        await using var routes = await Routes.Start();
        var now = routes.Time.GetUtcNow();
        var code = await Machines.NewCode(routes);

        var response = await Machines.Enroll(routes, Machines.Enrollment(code));

        // The machine's id and its key, never kept by a cache on the way.
        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.Equal("no-store", response.Response.Headers.CacheControl.ToString());
        var answer = (await Machines.Json(response)).AsObject();
        Assert.Equal(["machine_id", "key"], answer.Select(pair => pair.Key));
        var machine = Guid.Parse((string)answer["machine_id"]!);
        var key = (string)answer["key"]!;
        // dfm_ and the base64url of 32 random bytes, 47 characters.
        Assert.Matches(KeyShape(), key);
        Assert.Equal(47, key.Length);
        var random = Base64Url.DecodeFromChars(key.AsSpan(4));
        Assert.Equal(32, random.Length);

        // The table holds the hash, the machine, its user and when, and nothing else.
        await using var admin = await routes.Database.OpenAdmin(Ct);
        var columns = await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'machine_keys' ORDER BY column_name",
            cancellationToken: Ct));
        Assert.Equal(["created_at", "key_hash", "machine_id", "user_id"], columns);
        var row = await admin.QuerySingleAsync<(Guid Machine, Guid User, byte[] Hash, DateTime Made)>(new CommandDefinition(
            "SELECT machine_id, user_id, key_hash, created_at FROM machine_keys", cancellationToken: Ct));
        Assert.Equal(machine, row.Machine);
        Assert.Equal(Machines.User, row.User);
        Assert.Equal(Machines.Hash(key), row.Hash);
        Assert.Equal(now.UtcDateTime, row.Made, TimeSpan.FromMilliseconds(1));

        // Nothing anywhere in the database gives the key back, as text or as its bytes, nor the
        // code it was traded for.
        var tables = await admin.QueryAsync<string>(new CommandDefinition(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'",
            cancellationToken: Ct));
        var hex = Convert.ToHexStringLower(random);
        foreach (var table in tables)
        {
            var found = await admin.QuerySingleAsync<long>(new CommandDefinition(
                $"SELECT count(*) FROM \"{table}\" t WHERE strpos(t::text, @key) > 0 OR strpos(t::text, @hex) > 0 OR strpos(t::text, @code) > 0",
                new { key = key[4..], hex, code = code[4..] },
                cancellationToken: Ct));
            Assert.True(found == 0, $"{table} holds the key or the code");
        }

        // Nothing answers it again: not the list, and not hello.
        Assert.DoesNotContain(key[4..], (await Machines.List(routes)).ToJsonString(), StringComparison.Ordinal);
        var hello = await Machines.SayHello(routes, key);
        Assert.Equal(StatusCodes.Status200OK, hello.Response.StatusCode);
        Assert.DoesNotContain(key[4..], await Harness.Body(hello), StringComparison.Ordinal);

        // A second machine is another key.
        var (_, second) = await Machines.Enrolled(routes, "second");
        Assert.NotEqual(key, second);
    }

    [Fact]
    public async Task AKeyAndACodeAreComparedInConstantTime()
    {
        // No code that holds a key or a code compares strings or spans for equality, which is where
        // one compared with a stored value in ordinary time would show: each is hashed, and the
        // hash looked up.
        var equality = new List<MethodBase>();
        equality.AddRange(typeof(string).GetMethods().Where(m => m.Name is "Equals" or "op_Equality" or "op_Inequality" or "Compare" or "CompareOrdinal" or "CompareTo"));
        equality.AddRange(typeof(MemoryExtensions).GetMethods().Where(m => m.Name is "SequenceEqual" or "Equals" or "SequenceCompareTo" or "CompareTo")
            .Select(m => m.IsGenericMethodDefinition ? m.MakeGenericMethod(typeof(char)) : m));
        equality.AddRange(typeof(MemoryExtensions).GetMethods().Where(m => m.Name is "SequenceEqual" && m.IsGenericMethodDefinition)
            .Select(m => m.MakeGenericMethod(typeof(byte))));
        equality.AddRange(typeof(Enumerable).GetMethods().Where(m => m.Name == "SequenceEqual")
            .SelectMany(m => new[] { m.MakeGenericMethod(typeof(char)), m.MakeGenericMethod(typeof(byte)) }));
        equality.AddRange(typeof(StringComparer).GetMethods().Where(m => m.Name is "Equals" or "Compare"));
        equality.AddRange(typeof(object).GetMethods().Where(m => m.Name == "Equals"));

        var assembly = typeof(DevFlowSettings).Assembly;
        string[] holders = ["MachineKeys", "MachineEnrollments", "MachineAuthenticationHandler"];
        foreach (var holder in holders)
        {
            Assert.NotNull(assembly.GetTypes().SingleOrDefault(type => type.Namespace == "DevFlow.Machines" && type.Name == holder));
        }

        var comparing = IlScan.MethodsCalling(assembly, equality)
            .Where(method => IlScan.Outermost(method.DeclaringType!) is { Namespace: "DevFlow.Machines" } outer && holders.Contains(outer.Name))
            .Select(method => $"{IlScan.Outermost(method.DeclaringType!).Name}.{method.Name}")
            .ToList();
        Assert.Empty(comparing);
    }
}