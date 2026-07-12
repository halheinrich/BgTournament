namespace BgTournament.Api;

/// <summary>
/// The response to a key-issuing roster action (<c>POST /roster</c> and
/// <c>POST /roster/{name}/rotate</c>): the entry plus the issued engine key.
///
/// <para><b>Shown exactly once.</b> The server retains only a salted hash of
/// the key; this response is the sole time the plaintext exists outside the
/// engine author's hands. Deliver it out-of-band and store it like the secret
/// it is — a lost key is recovered by rotation, never by retrieval.</para>
/// </summary>
/// <param name="Entry">The roster entry the key belongs to.</param>
/// <param name="EngineKey">The issued key — the value the engine presents as <c>hello.engineKey</c>.</param>
public sealed record EngineKeyGrant(RosterEntry Entry, string EngineKey);
