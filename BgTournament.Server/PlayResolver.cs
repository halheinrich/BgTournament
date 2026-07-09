using BgDataTypes_Lib;
using BgMoveGen;

namespace BgTournament.Server;

/// <summary>
/// Resolves a wire-derived (hit-less) play to the canonical, hit-encoded
/// candidate from <see cref="MoveGenerator.GeneratePlays"/> — the sole
/// sanctioned consumer of <c>WireMapping.ToUnresolvedPlay</c>'s output.
///
/// <para>This step is load-bearing, not cosmetic: the wire deliberately
/// encodes no hits (they are board-derived facts the server reconstructs), so
/// a wire play never carries the hit signs the substrate's hit-sensitive
/// <see cref="Play"/> equality demands. Resolution matches by
/// <see cref="WirePlayKey"/> — the wire's own encoding altitude — and returns
/// the generator's hit-encoded candidate, which is what gets applied and what
/// the transcript records.</para>
/// </summary>
internal static class PlayResolver
{
    /// <summary>
    /// The canonical legal play whose wire encoding matches
    /// <paramref name="unresolved"/> for this board and dice, or null when no
    /// legal candidate matches.
    /// </summary>
    public static Play? Resolve(BoardState board, int die1, int die2, Play unresolved)
    {
        ArgumentNullException.ThrowIfNull(board);
        var probe = WirePlayKey.FromPlay(unresolved);
        foreach (var candidate in MoveGenerator.GeneratePlays(board, die1, die2))
        {
            if (WirePlayKey.FromPlay(candidate).Equals(probe))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// A play as the wire encodes it: the unordered multiset of single-die
    /// hops with hits stripped — <c>(from, |to|)</c> pairs, the exact
    /// projection <c>WireMapping.ToWireMoves</c> emits (PROTOCOL.md §6: each
    /// move is one die's hop, order does not matter, hits are not encoded).
    /// Two plays resolve to each other iff their keys are equal.
    ///
    /// <para><b>Hit-insensitive</b> because the wire cannot express hits, and
    /// <b>route-sensitive</b> because the hops are kept raw: two legal
    /// candidates can differ only in their hits (with a 3-2, 13/10*/8 hits en
    /// route where 13/11/8 does not), and the intermediate points the engine
    /// named are the only disambiguator. That is why this is deliberately
    /// <em>not</em> <see cref="Play"/> equality on hit-stripped moves:
    /// canonical chain-merging (<see cref="CanonicalPlay"/>) would collapse
    /// both routes to 13/8 and could resolve the wrong candidate.</para>
    ///
    /// <para>At most one candidate can match a key: candidates carrying the
    /// same hop multiset would reach the same final board (a hit is the
    /// board's fact about a landing point, not the encoding's choice), and
    /// the generator emits one candidate per distinct final board.</para>
    /// </summary>
    private readonly struct WirePlayKey : IEquatable<WirePlayKey>
    {
        // Fixed buffer, CanonicalPlay-style: max 4 hops (doubles). Unused
        // slots stay default(Move), so field equality is sequence equality.
        private readonly Move _h0, _h1, _h2, _h3;
        private readonly int _count;

        private WirePlayKey(ReadOnlySpan<Move> hops)
        {
            _count = hops.Length;
            if (_count > 0) _h0 = hops[0];
            if (_count > 1) _h1 = hops[1];
            if (_count > 2) _h2 = hops[2];
            if (_count > 3) _h3 = hops[3];
        }

        /// <summary>
        /// Projects <paramref name="play"/> onto its wire encoding. A caller
        /// matching one play against many should hoist its key out of the loop.
        /// </summary>
        public static WirePlayKey FromPlay(in Play play)
        {
            int n = play.Count;
            Span<Move> hops = stackalloc Move[n];
            for (int i = 0; i < n; i++)
            {
                hops[i] = new Move(play[i].FrPt, Math.Abs(play[i].ToPt));
            }

            // Order-insensitivity via a deterministic sort (FrPt desc, ToPt
            // desc — the generator's hop order; any total order would do).
            for (int i = 1; i < n; i++)
            {
                for (int j = i; j > 0 && Precedes(hops[j], hops[j - 1]); j--)
                {
                    (hops[j], hops[j - 1]) = (hops[j - 1], hops[j]);
                }
            }

            return new WirePlayKey(hops);
        }

        private static bool Precedes(Move a, Move b)
            => a.FrPt != b.FrPt ? a.FrPt > b.FrPt : a.ToPt > b.ToPt;

        /// <inheritdoc/>
        public bool Equals(WirePlayKey other)
            => _count == other._count
               && _h0 == other._h0 && _h1 == other._h1
               && _h2 == other._h2 && _h3 == other._h3;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is WirePlayKey key && Equals(key);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(_count, _h0, _h1, _h2, _h3);
    }
}
