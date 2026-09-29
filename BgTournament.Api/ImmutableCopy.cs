using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace BgTournament.Api;

/// <summary>
/// The one way this assembly's records take in a collection: an immutable
/// copy, made where the record is built or re-initialized. A record then
/// hands out nothing its caller can change afterwards, and nothing a cast can
/// write — an <see cref="ImmutableArray{T}"/> seen as an
/// <see cref="IReadOnlyList{T}"/> is still immutable.
///
/// <para>It copies; it does not validate. A null stays null, so a request
/// body missing a list reaches the server's own validation exactly as
/// before, and the JSON every shape writes is unchanged.</para>
///
/// <para><b>Field order.</b> A positional record copies a list by redeclaring
/// its property in the record's body, which puts that property after the
/// synthesized ones in reflection order — the order the serializer writes
/// fields in. Where that would move a field, <see cref="JsonPropertyOrderAttribute"/>
/// on the redeclared property, and on the positional ones that must follow
/// it, keeps each shape's field order as <c>ApiGoldenTests</c> pins it.</para>
/// </summary>
internal static class ImmutableCopy
{
    /// <summary>
    /// An immutable copy of <paramref name="source"/> in its order, or null
    /// for null. An immutable array is taken as it is, since it cannot change.
    /// </summary>
    [return: NotNullIfNotNull(nameof(source))]
    internal static IReadOnlyList<T>? Of<T>(IReadOnlyList<T>? source) =>
        source is null ? null : ImmutableArray.CreateRange(source);
}
