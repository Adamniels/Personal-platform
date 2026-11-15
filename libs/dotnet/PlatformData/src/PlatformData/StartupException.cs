namespace PlatformData;

/// <summary>
/// Why a service refuses to start or to migrate, its message naming what is wrong and never a
/// password: here a database it cannot reach as the role a setting names, the message starting
/// with that setting's name. A service throws it for its own reasons too, a setting that does not
/// parse among them, so that it has one such exception; its entry point prints the message and
/// exits with 1.
/// </summary>
public sealed class StartupException(string message) : Exception(message);