namespace DevFlow.Settings;

/// <summary>
/// What Dev flow treats as its code host, from <c>DEVFLOW_CODE_HOST</c>: <c>fake</c>, the default,
/// or <c>github</c>. The vault reads it for one rule alone, whether a key must be set; the code host
/// slice, #273, owns everything else it decides. See "Settings" in devflow/README.md.
/// </summary>
public enum CodeHost
{
    Fake,
    GitHub,
}