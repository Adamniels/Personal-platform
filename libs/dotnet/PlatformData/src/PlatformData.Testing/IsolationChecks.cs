using System.Reflection;

using Dapper;

using Npgsql;

namespace PlatformData.Testing;

/// <summary>
/// The checks every service with a database runs on itself, so that isolation between users is
/// not only true of the door but of every table and every type the service has. Each returns what
/// it found, and the service's test asserts it is empty, or zero; each service also keeps a test
/// that the check finds what it should, so a check cannot pass by checking nothing. See "Running
/// its tests" in libs/dotnet/PlatformData/README.md.
/// </summary>
public static class IsolationChecks
{
    /// <summary>The ledger, which holds nothing about a person, and which every service has.</summary>
    public const string Ledger = "schema_migrations";

    /// <summary>
    /// Each table in <c>public</c> of <paramref name="database"/>, but the ledger and
    /// <paramref name="unguarded"/>, and why it is not guarded: row level security not enabled or
    /// not forced, no <c>user_id uuid NOT NULL</c>, or anything but exactly one policy, the door's:
    /// permissive, for every command and every role, reading and checking rows by
    /// <see cref="UserDb.GuardFor"/> for the database's service. A near copy, such as the bare cast
    /// that errors outside the door instead of returning nothing, is not the door's.
    /// </summary>
    /// <param name="unguarded">The tables the service deliberately leaves unguarded, named in its
    /// own test; empty for most. Nothing else is allowed by default.</param>
    public static async Task<List<string>> Unguarded(TestDatabase database, IReadOnlyCollection<string> unguarded, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(unguarded);
        await using var admin = await database.OpenAdmin(cancellationToken);
        var guard = UserDb.GuardFor(database.Service);
        var door = await DoorPolicy(admin, guard, cancellationToken);
        var tables = await admin.QueryAsync<(string Name, bool Enabled, bool Forced, bool HasUserId)>(new CommandDefinition(
            """
            SELECT c.relname::text, c.relrowsecurity, c.relforcerowsecurity,
                   EXISTS (
                       SELECT FROM pg_attribute a
                       WHERE a.attrelid = c.oid AND a.attname = 'user_id'
                         AND a.atttypid = 'uuid'::regtype AND a.attnotnull
                   )
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
            ORDER BY c.relname COLLATE "C"
            """,
            cancellationToken: cancellationToken));
        var policies = (await admin.QueryAsync<Policy>(new CommandDefinition(
            """
            SELECT tablename::text AS "Table", policyname::text AS "Name", permissive AS "Permissive",
                   cmd AS "Command", roles::text AS "Roles", qual AS "Using", with_check AS "Check"
            FROM pg_policies WHERE schemaname = 'public' ORDER BY tablename COLLATE "C", policyname COLLATE "C"
            """,
            cancellationToken: cancellationToken))).ToList();

        var found = new List<string>();
        foreach (var (name, enabled, forced, hasUserId) in tables.Where(t => t.Name != Ledger && !unguarded.Contains(t.Name)))
        {
            if (!enabled)
            {
                found.Add($"{name}: row level security is not enabled");
            }

            if (!forced)
            {
                found.Add($"{name}: row level security is not forced");
            }

            if (!hasUserId)
            {
                found.Add($"{name}: no user_id uuid NOT NULL");
            }

            var own = policies.Where(p => p.Table == name).ToList();
            if (own.Count != 1)
            {
                found.Add($"{name}: {own.Count} policies, not exactly the door's one");
            }

            found.AddRange(own.Where(p => p with { Table = door.Table, Name = door.Name } != door).Select(p => $"{name}: policy {p.Name} is not the door's"));
        }

        return found;
    }

    /// <summary>
    /// How many tables, sequences, views and schemas in <paramref name="database"/> the app role
    /// owns, which must be none: an owner bypasses row level security by default, and could
    /// change a policy. That it cannot create one, through the door, is the service's own test.
    /// </summary>
    public static async Task<long> OwnedByTheAppRole(TestDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var admin = await database.OpenAdmin(cancellationToken);
        return await admin.QuerySingleAsync<long>(new CommandDefinition(
            """
            SELECT (SELECT count(*) FROM pg_class WHERE relowner = @role::regrole)
                 + (SELECT count(*) FROM pg_namespace WHERE nspowner = @role::regrole)
            """,
            new { role = database.AppRole },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Every place in <paramref name="types"/> outside <paramref name="allowed"/>, and the types
    /// nested in them, compiler generated ones included, where a member mentions a connection or a
    /// data source: a field, a property, an event, a method's or constructor's parameter, return
    /// or local, or a base type or interface, generic arguments and arrays included. An async
    /// method's locals are fields of its state machine, a nested type, and a lambda's captures
    /// fields of its closure. A service passes its assembly's types and allows none, since the
    /// door, <see cref="UserDb"/>, is the library's.
    /// </summary>
    /// <returns>One line per place, <c>&lt;type&gt;: &lt;where&gt;</c>, the type named without its
    /// namespace.</returns>
    public static IEnumerable<string> ConnectionHolders(IEnumerable<Type> types, params Type[] allowed)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(allowed);
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in types.Where(t => !allowed.Any(outer => IsWithin(t, outer))))
        {
            var uses = new List<(string Where, Type Used)>();
            if (type.BaseType is { } baseType)
            {
                uses.Add(("base type", baseType));
            }

            uses.AddRange(type.GetInterfaces().Select(i => ("interface", i)));
            uses.AddRange(type.GetFields(Declared).Select(f => ($"field {f.Name}", f.FieldType)));
            uses.AddRange(type.GetProperties(Declared).Select(p => ($"property {p.Name}", p.PropertyType)));
            uses.AddRange(type.GetEvents(Declared).Select(e => ($"event {e.Name}", e.EventHandlerType!)));
            foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                if (method is MethodInfo info)
                {
                    uses.Add(($"return of {method.Name}", info.ReturnType));
                }

                uses.AddRange(method.GetParameters().Select(p => ($"parameter of {method.Name}", p.ParameterType)));
                uses.AddRange((method.GetMethodBody()?.LocalVariables ?? []).Select(l => ($"local in {method.Name}", l.LocalType)));
            }

            foreach (var where in uses.Where(use => Mentions(use.Used)).Select(use => use.Where).Distinct())
            {
                yield return $"{(type.Namespace is { } ns ? type.FullName![(ns.Length + 1)..] : type.FullName)}: {where}";
            }
        }
    }

    /// <summary>A row of pg_policies.</summary>
    private sealed record Policy(string Table, string Name, string Permissive, string Command, string Roles, string? Using, string? Check);

    /// <summary>The door's policy, as Postgres deparses it into pg_policies, so a table's is
    /// compared with it exactly. Read from a temporary table in a transaction rolled back.</summary>
    private static async Task<Policy> DoorPolicy(NpgsqlConnection admin, string guard, CancellationToken cancellationToken)
    {
        await using var transaction = await admin.BeginTransactionAsync(cancellationToken);
        await admin.ExecuteAsync(new CommandDefinition(
            $"CREATE TEMP TABLE door_shape (user_id uuid); CREATE POLICY door_shape ON door_shape USING ({guard}) WITH CHECK ({guard})",
            transaction: transaction, cancellationToken: cancellationToken));
        var policy = await admin.QuerySingleAsync<Policy>(new CommandDefinition(
            """
            SELECT tablename::text AS "Table", policyname::text AS "Name", permissive AS "Permissive",
                   cmd AS "Command", roles::text AS "Roles", qual AS "Using", with_check AS "Check"
            FROM pg_policies WHERE tablename = 'door_shape' AND schemaname LIKE 'pg_temp%'
            """,
            transaction: transaction, cancellationToken: cancellationToken));
        await transaction.RollbackAsync(cancellationToken);
        // What every table's policy is compared with, so it must itself be the door's shape:
        // permissive, for every command and every role.
        if ((policy.Permissive, policy.Command, policy.Roles) != ("PERMISSIVE", "ALL", "{public}"))
        {
            throw new InvalidOperationException($"the door's policy deparsed as {policy}, not permissive for every command and role");
        }

        return policy;
    }

    private static bool Mentions(Type type) =>
        type.IsAssignableTo(typeof(NpgsqlConnection))
        || type.IsAssignableTo(typeof(NpgsqlDataSource))
        || (type.HasElementType && Mentions(type.GetElementType()!))
        || (type.IsConstructedGenericType && type.GetGenericArguments().Any(Mentions));

    private static bool IsWithin(Type type, Type outer)
    {
        for (Type? at = type; at is not null; at = at.DeclaringType)
        {
            if (at == outer)
            {
                return true;
            }
        }

        return false;
    }
}