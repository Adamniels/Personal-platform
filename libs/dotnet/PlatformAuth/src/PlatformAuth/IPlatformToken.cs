namespace PlatformAuth;

/// <summary>
/// The request's verified token, a request feature set when authentication succeeds: read it with
/// <c>HttpContext.Features.Get&lt;IPlatformToken&gt;()</c>. <see cref="Token"/> is the string as
/// the request carried it, so a service can forward it to another service, such as the brain, on
/// the caller's behalf. It is never logged.
/// </summary>
public interface IPlatformToken
{
    string Token { get; }

    Claims Claims { get; }
}

internal sealed record PlatformToken(string Token, Claims Claims) : IPlatformToken;