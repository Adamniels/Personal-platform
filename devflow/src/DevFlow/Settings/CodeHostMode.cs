using System.Text.Json.Serialization;

namespace DevFlow.Settings;

/// <summary>
/// Which code host Dev flow talks to, from <c>DEVFLOW_CODE_HOST</c>: <c>fake</c>, the default, the
/// in memory <see cref="CodeHost.FakeCodeHost"/>, or <c>github</c>. Unset is the fake, so the
/// laptop's tests, CI and the runner's stack need nothing set; the hosted copy names its mode. On
/// the wire, in <c>GET /devflow/api/status</c>, it is the setting's own word. See "Settings" in
/// devflow/README.md.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CodeHostMode>))]
public enum CodeHostMode
{
    [JsonStringEnumMemberName("fake")]
    Fake,

    [JsonStringEnumMemberName("github")]
    GitHub,
}