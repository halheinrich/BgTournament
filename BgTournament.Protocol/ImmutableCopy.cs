using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace BgTournament.Protocol;

/// <summary>
/// The one way this assembly's messages take in a collection: an immutable
/// copy, made where the message is initialized. A message then hands out
/// nothing its caller can change afterwards, and nothing a cast can write —
/// an <see cref="ImmutableArray{T}"/> seen as an
/// <see cref="IReadOnlyList{T}"/> is still immutable.
///
/// <para>It copies; it does not validate. A null stays null, so a frame
/// carrying a null list is handled exactly as before (a play reply's null
/// moves are the engine's contract violation), and the wire text every
/// message writes is unchanged.</para>
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
