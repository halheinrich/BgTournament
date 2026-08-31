using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// The string-token-exact enum converter: a
/// <see cref="JsonStringEnumConverter{TEnum}"/> that rejects numeric tokens on
/// read (and refuses to write an undefined value as a number), for
/// attribute-form registration — where the base type's
/// <c>allowIntegerValues: false</c> knob is otherwise unreachable, because an
/// attribute can only name a converter type, not pass it constructor arguments.
///
/// <para>Every writer of these enums emits the string pinned per member with
/// <see cref="JsonStringEnumMemberNameAttribute"/>, and the reader is the
/// inverse of its writer: it accepts those names and nothing else. The base
/// converter's default would also accept an integer ordinal on read — silently
/// re-coupling wire and durable payloads to member declaration numbering,
/// which the contracts reserve the right to change
/// (halheinrich/backgammon#164; no writer here ever emits an ordinal, so
/// accepting one could only ever mask corruption or decode a stale numbering).</para>
///
/// <para>Deliberately no naming policy: the per-member pins are the single
/// source of truth, and a member missing its pin should surface loudly in the
/// golden tests as its PascalCase declared name rather than be silently
/// camelCased. Name matching on read stays case-insensitive — the base
/// converter's behavior, which has no knob to change — so the strictness
/// closed here is token kind, not case.</para>
/// </summary>
/// <typeparam name="TEnum">The enum type the converter handles.</typeparam>
public sealed class StrictJsonStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>Creates the converter; attribute-form registration uses this.</summary>
    public StrictJsonStringEnumConverter()
        : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}
