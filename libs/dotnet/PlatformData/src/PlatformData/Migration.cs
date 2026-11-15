using System.Globalization;

namespace PlatformData;

/// <summary>
/// One file of a service's migrations/, <c>NNNN_snake_description.sql</c>: its version, the four
/// digits, its name, the rest before <c>.sql</c>, and the SQL it holds.
/// </summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>The file's name, which the version and the name spell exactly.</summary>
    public string File => FileName(Version, Name);

    internal static string FileName(int version, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{version:D4}_{name}.sql");
}