using System.Text.Json;

using DevFlow.Jobs;

namespace DevFlow.Tests.Jobs;

/// <summary>
/// Making a worker's text storable: only a NUL and a lone half of a surrogate pair change, and
/// JSON written again keeps every other escape, number and literal as it came. See "Jobs" in
/// devflow/README.md.
/// </summary>
public sealed class StorableTests
{
    /// <summary>A fact rather than inline data: an attribute keeps its strings as UTF-8, which
    /// cannot hold a lone surrogate.</summary>
    [Fact]
    public void TextReplacesOnlyWhatPostgresCannotHold()
    {
        var r = Storable.Replacement;
        Assert.Equal("plain", Storable.Text("plain"));
        Assert.Equal($"a{r}b", Storable.Text("a\0b"));
        Assert.Equal($"{r}", Storable.Text("\ud800"));
        Assert.Equal($"x{r}", Storable.Text("x\udc00"));
        Assert.Equal($"{r}{r}", Storable.Text("\udc00\ud800"));
        Assert.Equal("\U0001F600 kept", Storable.Text("\U0001F600 kept"));
    }

    [Fact]
    public void JsonKeepsEverythingElseAsItCame()
    {
        const string Raw = """
            {"q\"\\\/\b\f\n\r\téA":"q\"\\\/\b\f\n\r\téA😀",
             "n":1.50,"big":12345678901234567890,"e":-2.5E+3,"t":true,"f":false,"z":null,
             "l":[1,{"x":"\u0000","y":"\ud800"}],"empty":{}}
            """;
        using var document = JsonDocument.Parse(Raw);

        var stored = Storable.Json(document.RootElement);

        const string Escapes = "q\"\\/\b\f\n\r\téA";
        Assert.Equal(Escapes + "\U0001F600", stored.GetProperty(Escapes).GetString());
        Assert.Equal("1.50", stored.GetProperty("n").GetRawText());
        Assert.Equal("12345678901234567890", stored.GetProperty("big").GetRawText());
        Assert.Equal("-2.5E+3", stored.GetProperty("e").GetRawText());
        Assert.Equal(JsonValueKind.True, stored.GetProperty("t").ValueKind);
        Assert.Equal(JsonValueKind.False, stored.GetProperty("f").ValueKind);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("z").ValueKind);
        var inner = stored.GetProperty("l")[1];
        Assert.Equal($"{Storable.Replacement}", inner.GetProperty("x").GetString());
        Assert.Equal($"{Storable.Replacement}", inner.GetProperty("y").GetString());
        Assert.Equal(JsonValueKind.Object, stored.GetProperty("empty").ValueKind);
    }
}