using System.Net.Sockets;
using System.Net.WebSockets;
using BgTournament.EngineClient;
using Microsoft.Extensions.Logging;
using EngineClientSdk = BgTournament.EngineClient.EngineClient;

namespace BgTournament.ReferenceBot;

/// <summary>
/// The reference-bot executable's entry point: parse the command line, compose
/// the SDK client (<see cref="ReferenceBot.CreateClient"/>), connect, and serve
/// until the server closes the connection or the operator interrupts. Every
/// failure resolves to a named <see cref="ExitCode"/> — never a stack trace.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (Array.Exists(args, a => a is "--help" or "-h"))
        {
            Console.WriteLine(ReferenceBot.UsageText);
            return (int)ExitCode.Success;
        }

        ReferenceBotOptions options;
        try
        {
            options = ReferenceBotOptions.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ReferenceBot.UsageText);
            return (int)ExitCode.UsageError;
        }

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(options.Verbosity)
            .AddConsole());
        var logger = loggerFactory.CreateLogger<EngineClientSdk>();

        // Ctrl-C cancels the session gracefully — cancel the token and let the
        // serve loop unwind, rather than letting the runtime kill the process.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var client = ReferenceBot.CreateClient(
            options,
            onDiceVerified: report => Console.WriteLine(ReferenceBot.DescribeDiceReport(report)),
            logger: logger);

        Console.WriteLine(
            $"Connecting to {options.ServerUri} as \"{options.Name}\" (seed {options.Seed})...");

        try
        {
            await client.RunAsync(options.ServerUri, cts.Token);
            return (int)ExitCode.Success;
        }
        catch (HandshakeRejectedException ex)
        {
            Console.Error.WriteLine($"Handshake rejected by the server: {ex.Reason}");
            return (int)ExitCode.HandshakeRejected;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.WriteLine("Interrupted; disconnecting.");
            return (int)ExitCode.Canceled;
        }
        catch (Exception ex) when (ex is WebSocketException or SocketException or IOException)
        {
            Console.Error.WriteLine($"Could not maintain the connection to {options.ServerUri}: {ex.Message}");
            return (int)ExitCode.ConnectionFailed;
        }
    }
}
