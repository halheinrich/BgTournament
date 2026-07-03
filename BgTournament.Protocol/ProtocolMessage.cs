using System.Text.Json.Serialization;

namespace BgTournament.Protocol;

/// <summary>
/// Base type for every message on the BgTournament wire. The JSON form of a
/// message is a single flat object carrying a <c>"type"</c> discriminator that
/// names the concrete message.
///
/// <para>The canonical, language-neutral wire contract is <c>PROTOCOL.md</c>
/// at the repo root; these types are its .NET binding. Serialize and
/// deserialize exclusively through <see cref="WireProtocol"/> so the
/// discriminator and wire conventions are always applied.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(RejectedMessage), "rejected")]
[JsonDerivedType(typeof(PlayQueryMessage), "playQuery")]
[JsonDerivedType(typeof(PlayReplyMessage), "playReply")]
[JsonDerivedType(typeof(CubeOfferQueryMessage), "cubeOfferQuery")]
[JsonDerivedType(typeof(CubeOfferReplyMessage), "cubeOfferReply")]
[JsonDerivedType(typeof(CubeResponseQueryMessage), "cubeResponseQuery")]
[JsonDerivedType(typeof(CubeResponseReplyMessage), "cubeResponseReply")]
[JsonDerivedType(typeof(MatchStartedMessage), "matchStarted")]
[JsonDerivedType(typeof(MatchEndedMessage), "matchEnded")]
public abstract record ProtocolMessage;
