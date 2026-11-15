namespace DevFlow;

/// <summary>
/// Why the vault refused to open a secret: its row names a key that is not configured, or it does
/// not open, having been changed or moved from another user or purpose. The message names the
/// purpose and the key's id, never the value or the key.
/// </summary>
public sealed class VaultException(string message) : Exception(message);