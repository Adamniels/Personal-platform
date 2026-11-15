using PlatformData;

namespace DevFlow.Settings;

/// <summary>
/// The vault's keys, from <c>DEVFLOW_VAULT_KEY</c> and <c>DEVFLOW_VAULT_OLD_KEYS</c>, read by
/// <see cref="DevFlowSettings.Load"/>. A put seals with <see cref="Current"/>; a get opens with
/// whichever of <see cref="Current"/> and <see cref="Old"/> its row names. Without a key, the fake
/// code host makes one in memory and the GitHub one refuses to start. See "The vault" in
/// devflow/README.md.
/// </summary>
public sealed class VaultSettings
{
    public const string KeySetting = "DEVFLOW_VAULT_KEY";
    public const string OldKeysSetting = "DEVFLOW_VAULT_OLD_KEYS";

    /// <summary>The id a key made in memory is recorded by, so a row sealed with one and read
    /// after a restart is refused naming it.</summary>
    public const string GeneratedId = "memory";

    private VaultSettings(VaultKey current, IReadOnlyList<VaultKey> old, bool generated)
    {
        Current = current;
        Old = old;
        Generated = generated;
    }

    /// <summary>The key every put seals with.</summary>
    public VaultKey Current { get; }

    /// <summary>Keys rows may still be sealed with, opened but never sealed with, in the order
    /// listed.</summary>
    public IReadOnlyList<VaultKey> Old { get; }

    /// <summary>Whether <see cref="Current"/> was made at startup, since no key was set, so what
    /// is sealed with it is lost when the process stops.</summary>
    public bool Generated { get; }

    /// <summary>
    /// The keys from <paramref name="read"/>. Each setting empty is the same as unset, since the
    /// hosted copy passes both with <c>${VAR:-}</c>, which is empty when its <c>.env</c> leaves
    /// the variable out.
    /// </summary>
    /// <exception cref="StartupException">A key does not parse, an id is listed twice, old keys
    /// are set without a current one, or no key is set and <paramref name="codeHost"/> is GitHub.
    /// The message starts with the setting's name and never repeats its value.</exception>
    internal static VaultSettings Load(Func<string, string?> read, CodeHostMode codeHost)
    {
        var current = NotEmpty(read(KeySetting));
        var oldList = NotEmpty(read(OldKeysSetting));
        if (current is null)
        {
            if (codeHost == CodeHostMode.GitHub)
            {
                throw new StartupException(
                    $"{KeySetting}: is not set, and Dev flow with DEVFLOW_CODE_HOST=github holds real secrets, so it needs a key that outlives it; make one with printf 'k1:%s\\n' \"$(openssl rand -base64 32)\"");
            }

            if (oldList is not null)
            {
                throw new StartupException($"{KeySetting}: is not set, and {OldKeysSetting} is, which only opens rows a current key replaces");
            }

            return new VaultSettings(VaultKey.Generate(GeneratedId), [], generated: true);
        }

        var key = Parse(KeySetting, current);
        var old = new List<VaultKey>();
        if (oldList is not null)
        {
            var entries = oldList.Split(',');
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = Parse(OldKeysSetting, entries[i], $"entry {i + 1}: ");
                if (entry.Id == key.Id)
                {
                    throw new StartupException($"{OldKeysSetting}: entry {i + 1}: has the id {entry.Id}, which {KeySetting} has");
                }

                if (old.Any(other => other.Id == entry.Id))
                {
                    throw new StartupException($"{OldKeysSetting}: entry {i + 1}: has the id {entry.Id}, which an earlier entry has");
                }

                old.Add(entry);
            }
        }

        return new VaultSettings(key, old, generated: false);
    }

    /// <summary>The ids alone, never a key.</summary>
    public override string ToString() =>
        $"VaultSettings {{ Current = {Current.Id}, Old = [{string.Join(", ", Old.Select(key => key.Id))}], Generated = {Generated} }}";

    private static string? NotEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static VaultKey Parse(string name, string text, string where = "")
    {
        try
        {
            return VaultKey.Parse(text);
        }
        catch (FormatException refused)
        {
            throw new StartupException($"{name}: {where}{refused.Message}");
        }
    }
}