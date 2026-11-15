namespace DevFlow.Settings;

/// <summary>
/// Why Dev flow refuses to start: a setting that is set and does not parse, its message starting
/// with the setting's name, or a missing build of the page. <c>Program</c> prints the message and
/// exits with 1.
/// </summary>
public sealed class StartupException(string message) : Exception(message);