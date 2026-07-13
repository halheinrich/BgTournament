using System.Net.WebSockets;
using BgTournament.EngineClient;
using BgTournament.ReferenceBot;
using Microsoft.AspNetCore.Mvc.Testing;
using Bot = BgTournament.ReferenceBot.ReferenceBot;

namespace BgTournament.Tests;

/// <summary>
/// The reference-bot executable's host seam: its option parsing, its fair-dice
/// report text, and — the arc deliverable — an in-proc smoke proving the exe's
/// composition (<see cref="ReferenceBot.CreateClient"/>) connects and plays a
/// full match over the real wire, exactly as <c>Program</c> wires it.
///
/// <para>The Server's <c>Program</c> is a global-namespace top-level-statements
/// anchor; the bot's is <see cref="BgTournament.ReferenceBot"/>-namespaced, so
/// <c>WebApplicationFactory&lt;global::Program&gt;</c> names the server
/// unambiguously here.</para>
/// </summary>
public class ReferenceBotHostTests
{
    private static readonly TimeSpan SmokeDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ReferenceBotComposition_ConnectsAndPlaysAFairMatch_VerifyingItsDice()
    {
        using var factory = ServerHarness.NewFactory();
        var report = new TaskCompletionSource<DiceVerificationReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // The bot composed exactly as Program composes it (args → options →
        // CreateClient), served over the in-proc socket instead of a real one.
        await ConnectReferenceBotAsync(
            factory, name: "RefBot", seed: 101, onVerified: r => report.TrySetResult(r));
        // A well-behaved opponent so the match completes.
        await ServerHarness.RunWellBehavedClientAsync(factory, "Sparring", seed: 202, CancellationToken.None);
        await ServerHarness.WaitForEnginesAsync(factory, "RefBot", "Sparring");

        // A seedless match selects fair mode — exercising the bot's onDiceVerified hook.
        var match = await ServerHarness.StartFairMatchAsync(factory, "RefBot", "Sparring", matchLength: 3);
        string matchId = match.GetProperty("matchId").GetString()!;
        var summary = await ServerHarness.WaitForMatchEndAsync(factory, matchId, SmokeDeadline);
        Assert.Equal("completed", summary.GetProperty("status").GetString());

        // The bot's own fair-dice verification fired and passed — the showcase path.
        var verdict = await report.Task.WaitAsync(SmokeDeadline);
        Assert.True(verdict.Verified, $"{verdict.Outcome} — {verdict.Detail}");
        Assert.True(verdict.ObservedRollCount > 0, "The bot observed no rolls to verify.");
    }

    private static async Task ConnectReferenceBotAsync(
        WebApplicationFactory<global::Program> factory,
        string name,
        int seed,
        Action<DiceVerificationReport> onVerified)
    {
        var options = ReferenceBotOptions.Parse(
            ["--server", "ws://localhost/engine", "--name", name, "--seed", seed.ToString()]);
        var socket = await factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/engine"), CancellationToken.None);
        var client = Bot.CreateClient(options, onDiceVerified: onVerified, logger: null);
        _ = Task.Run(async () =>
        {
            try
            {
                await client.ServeAsync(socket);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                // Test teardown ended the session.
            }
        });
    }

    [Fact]
    public void Parse_MinimalArgs_BindsRequiredAndDefaults()
    {
        var options = ReferenceBotOptions.Parse(["--server", "ws://localhost:5000/engine", "--name", "Bot"]);

        Assert.Equal(new Uri("ws://localhost:5000/engine"), options.ServerUri);
        Assert.Equal("Bot", options.Name);
        Assert.Equal(0, options.Seed);
        Assert.Null(options.Version);
        Assert.Null(options.Author);
        Assert.Null(options.EngineKey);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, options.Verbosity);
    }

    [Fact]
    public void Parse_AllOptions_Bind_InBothFormsAndAnyOrder()
    {
        var options = ReferenceBotOptions.Parse([
            "--name=Bot",
            "--author", "Jane Doe",
            "--seed", "-7",
            "--engine-key=deadbeef",
            "--server", "wss://arena.example/engine",
            "--version", "2.1",
            "--verbosity", "Debug",
        ]);

        Assert.Equal(new Uri("wss://arena.example/engine"), options.ServerUri);
        Assert.Equal("Bot", options.Name);
        Assert.Equal(-7, options.Seed);
        Assert.Equal("2.1", options.Version);
        Assert.Equal("Jane Doe", options.Author);
        Assert.Equal("deadbeef", options.EngineKey);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, options.Verbosity);
    }

    [Theory]
    [InlineData("--name", "Bot")]                                   // missing --server
    [InlineData("--server", "ws://localhost/engine")]              // missing --name
    public void Parse_MissingRequired_Throws(params string[] args)
    {
        Assert.Throws<UsageException>(() => ReferenceBotOptions.Parse(args));
    }

    [Theory]
    [InlineData("http://localhost/engine")]   // wrong scheme
    [InlineData("localhost:5000")]            // not absolute
    public void Parse_BadServerUri_Throws(string uri)
    {
        Assert.Throws<UsageException>(
            () => ReferenceBotOptions.Parse(["--server", uri, "--name", "Bot"]));
    }

    [Fact]
    public void Parse_NonIntegerSeed_Throws() =>
        Assert.Throws<UsageException>(() => ReferenceBotOptions.Parse(
            ["--server", "ws://localhost/engine", "--name", "Bot", "--seed", "abc"]));

    [Fact]
    public void Parse_UnknownVerbosity_Throws() =>
        Assert.Throws<UsageException>(() => ReferenceBotOptions.Parse(
            ["--server", "ws://localhost/engine", "--name", "Bot", "--verbosity", "loud"]));

    [Fact]
    public void Parse_UnknownFlag_Throws() =>
        Assert.Throws<UsageException>(() => ReferenceBotOptions.Parse(
            ["--server", "ws://localhost/engine", "--name", "Bot", "--turbo"]));

    [Fact]
    public void Parse_DuplicateOption_Throws() =>
        Assert.Throws<UsageException>(() => ReferenceBotOptions.Parse(
            ["--server", "ws://localhost/engine", "--name", "Bot", "--name", "Other"]));

    [Fact]
    public void Parse_FlagMissingValue_Throws() =>
        Assert.Throws<UsageException>(() => ReferenceBotOptions.Parse(
            ["--server", "ws://localhost/engine", "--name"]));

    [Fact]
    public void DescribeDiceReport_Verified_SaysPassedWithCount()
    {
        var text = Bot.DescribeDiceReport(
            new DiceVerificationReport(DiceVerificationOutcome.Verified, 42, null));

        Assert.Contains("PASSED", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeDiceReport_Failed_NamesOutcomeAndDetail()
    {
        var text = Bot.DescribeDiceReport(
            new DiceVerificationReport(DiceVerificationOutcome.CommitmentMismatch, 5, "the key did not match"));

        Assert.Contains("FAILED", text, StringComparison.Ordinal);
        Assert.Contains("CommitmentMismatch", text, StringComparison.Ordinal);
        Assert.Contains("the key did not match", text, StringComparison.Ordinal);
    }
}
