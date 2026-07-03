using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgTournament.Protocol;

/// <summary>
/// The wire protocol definition: its version constant and the single
/// serialization boundary for <see cref="ProtocolMessage"/> traffic.
///
/// <para>All wire (de)serialization goes through <see cref="Serialize"/> /
/// <see cref="Deserialize"/>. This is deliberate encapsulation, not
/// convenience: System.Text.Json emits the <c>"type"</c> discriminator only
/// when the declared type is the polymorphic base, so serializing a message
/// through its concrete type would silently drop the discriminator and
/// produce a frame no peer can dispatch.</para>
/// </summary>
public static class WireProtocol
{
    /// <summary>
    /// The wire protocol version this assembly implements. Exchanged in the
    /// handshake; the server rejects any <see cref="HelloMessage"/> carrying a
    /// different value.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Wire conventions (see PROTOCOL.md): camelCase properties, enums as
    /// camelCase strings (integer forms rejected), optional fields omitted
    /// when null, discriminator accepted in any position on read.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Serialize a message to its wire form (one JSON object, no indentation).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static string Serialize(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, Options);
    }

    /// <summary>
    /// Deserialize one wire frame. Any malformed frame — invalid JSON, JSON
    /// <c>null</c>, a missing or unknown <c>"type"</c>, a missing required
    /// field, or an out-of-range enum value — throws <see cref="JsonException"/>,
    /// so callers have a single failure type to translate into their
    /// protocol-violation handling.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">The frame is not a well-formed protocol message.</exception>
    public static ProtocolMessage Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<ProtocolMessage>(json, Options)
                ?? throw new JsonException("The frame is JSON null.");
        }
        catch (NotSupportedException ex)
        {
            // STJ reports a missing discriminator on an abstract base as
            // NotSupportedException; fold it into the single failure type.
            throw new JsonException("The frame is not a recognized protocol message.", ex);
        }
    }
}
