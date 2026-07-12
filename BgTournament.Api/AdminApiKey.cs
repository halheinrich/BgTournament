namespace BgTournament.Api;

/// <summary>
/// The admin surface's API-key convention — the one home of the header name,
/// so the server that checks it and every consumer that sends it speak the
/// same string (the <c>VerifiableDice</c> precedent, one contract layer up).
///
/// <para>Keys are named: the server's configuration maps each actor name to
/// its key value, and the name — never the key — becomes the actor identity
/// stamped on the durable record of the actions the key authorizes. A server
/// with no keys configured serves the admin surface anonymously; a server
/// with any key configured requires one on every admin request. A presented
/// key is always validated — an unknown key is refused even by an
/// anonymously-serving server, so a key mismatch between the two sides fails
/// loudly instead of silently degrading to anonymous access.</para>
/// </summary>
public static class AdminApiKey
{
    /// <summary>The request header carrying the admin API key.</summary>
    public const string HeaderName = "X-Api-Key";
}
