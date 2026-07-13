using BgTournament.EngineClient;
using Microsoft.Extensions.Logging;
using EngineClientSdk = BgTournament.EngineClient.EngineClient;

namespace BgTournament.ReferenceBot;

/// <summary>
/// The composition seam for the reference-bot executable: assembling the SDK
/// client from parsed options, and formatting the fair-dice report for display.
/// Kept separate from <c>Program</c> so the parts worth testing (composition,
/// report text) sit behind plain methods rather than inside <c>Main</c>.
/// </summary>
internal static class ReferenceBot
{
    /// <summary>
    /// Assemble an <see cref="EngineClientSdk"/> over the reference policies
    /// (<see cref="RandomPlayAgent"/> + <see cref="PassiveCubeAgent"/>) for the
    /// given options. This is exactly the composition <c>Program</c> connects to
    /// the wire — the in-proc smoke drives the same method over a test socket.
    /// </summary>
    /// <param name="options">The parsed, validated session inputs.</param>
    /// <param name="onDiceVerified">Optional fair-dice hook; the report is delivered here after each fair-mode match.</param>
    /// <param name="logger">Optional logger for the SDK's diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static EngineClientSdk CreateClient(
        ReferenceBotOptions options,
        Action<DiceVerificationReport>? onDiceVerified = null,
        ILogger<EngineClientSdk>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var identity = new EngineIdentity(options.Name, options.Version, options.Author);
        return new EngineClientSdk(
            identity,
            new RandomPlayAgent(options.Seed),
            new PassiveCubeAgent(),
            logger,
            onDiceVerified,
            options.EngineKey);
    }

    /// <summary>
    /// A one-line, human-readable summary of a fair-dice verification report —
    /// the showcase output when the server runs a match on provably-fair dice
    /// (PROTOCOL.md §8).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public static string DescribeDiceReport(DiceVerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Verified
            ? $"Dice verification PASSED: {report.ObservedRollCount} observed roll(s) matched the committed key."
            : $"Dice verification FAILED ({report.Outcome}): {report.Detail}";
    }

    /// <summary>The command-line usage text, shown on <c>--help</c> and on a usage error.</summary>
    public const string UsageText =
        """
        BgTournament reference bot — connects to a tournament server and plays with
        the reference policies (uniformly-random legal play; never doubles, always
        takes). It is the baseline opponent, a connection smoke test, and a worked
        example of the .NET SDK. See ONBOARDING.md for the walkthrough.

        Usage:
          BgTournament.ReferenceBot --server <uri> --name <name> [options]

        Required:
          --server <uri>       Engine endpoint, ws:// or wss:// (e.g. ws://localhost:5000/engine).
          --name <name>        Engine registry name; must be unique among connected engines.

        Options:
          --seed <int>         Seed for the random play policy (default: 0 — reproducible).
          --version <string>   Engine version label (handshake metadata).
          --author <string>    Author label (handshake metadata).
          --engine-key <hex>   Registration key, for a server enforcing the Registered policy.
          --verbosity <level>  SDK log level: trace|debug|information|warning|error|critical|none
                               (default: information).
          --help, -h           Show this help and exit.
        """;
}
