using BgTournament.Protocol;
using BgTournament.Server.Persistence;

namespace BgTournament.Tests;

/// <summary>
/// The server-session journal end to end: one segment per boot under
/// <c>server/</c>, headed by <c>started</c> (version-stamped) and closed by the
/// graceful <c>stopped</c> marker; engine registrations, disconnects, and
/// handshake rejections journaled with the same names and reasons the wire
/// carries. Evidence-only by design — a second boot opens a second segment
/// and rehydrates nothing from the first.
/// </summary>
public class ServerJournalTests
{
    private static string[] SegmentPaths(string dataDirectory)
    {
        string serverDirectory = Path.Combine(dataDirectory, "server");
        return Directory.Exists(serverDirectory)
            ? Directory.GetFiles(serverDirectory, "*.jsonl")
            : [];
    }

    private static async Task<ServerJournalEvent[]> ReadSegmentAsync(string path) =>
        (await File.ReadAllLinesAsync(path))
            .Select(JournalCodec.DeserializeServerEvent)
            .ToArray();

    [Fact]
    public async Task ServerSession_JournalsLifecycle_IntoOneSegment()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-serverjournal-").FullName;
        try
        {
            using (var factory = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                // Register (with identity), get a duplicate-name rejection,
                // then disconnect — three journaled moments, in that order.
                await using (var alpha = await TestEngine.ConnectAsync(factory, "Alpha"))
                {
                    await ServerHarness.WaitForEnginesAsync(factory, "Alpha");

                    await using var duplicate = await TestEngine.ConnectAsync(
                        factory, "Alpha", expectWelcome: false);
                    var rejection = await duplicate.ExpectAsync<RejectedMessage>();
                    Assert.Contains("already connected", rejection.Reason);

                    alpha.Abort();
                    await WaitForDisconnectAsync(factory);
                }
            }   // disposing the factory runs the graceful shutdown

            string segment = Assert.Single(SegmentPaths(dataDirectory));
            var events = await ReadSegmentAsync(segment);

            var header = Assert.IsType<ServerStartedEvent>(events[0]);
            Assert.Equal(JournalCodec.ServerSchemaVersion, header.SchemaVersion);

            var connected = Assert.Single(events.OfType<EngineConnectedEvent>());
            Assert.Equal("Alpha", connected.EngineName);

            var rejected = Assert.Single(events.OfType<HandshakeRejectedEvent>());
            Assert.Equal("Alpha", rejected.EngineName);
            Assert.Contains("already connected", rejected.Reason);

            var disconnected = Assert.Single(events.OfType<EngineDisconnectedEvent>());
            Assert.Equal("Alpha", disconnected.EngineName);

            // Graceful shutdown closes the segment; its absence would be
            // crash evidence.
            Assert.IsType<ServerStoppedEvent>(events[^1]);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SecondBoot_OpensASecondSegment()
    {
        string dataDirectory = Directory.CreateTempSubdirectory("bgtournament-serverjournal-").FullName;
        try
        {
            using (var first = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                using var http = first.CreateClient();
                await http.GetAsync("/engines"); // force the host to start
            }

            using (var second = ServerHarness.NewFactory(dataDirectory: dataDirectory))
            {
                using var http = second.CreateClient();
                await http.GetAsync("/engines");
            }

            var segments = SegmentPaths(dataDirectory);
            Assert.Equal(2, segments.Length);
            foreach (string segment in segments)
            {
                var events = await ReadSegmentAsync(segment);
                Assert.IsType<ServerStartedEvent>(events[0]);
                Assert.IsType<ServerStoppedEvent>(events[^1]);
            }
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    /// <summary>Wait until the registry no longer lists any engine (the abort was observed).</summary>
    private static async Task WaitForDisconnectAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        using var http = factory.CreateClient();
        var deadline = DateTime.UtcNow + ServerHarness.ReceiveDeadline;
        while (DateTime.UtcNow < deadline)
        {
            string engines = await http.GetStringAsync("/engines");
            if (engines == "[]")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"The engine was still listed after {ServerHarness.ReceiveDeadline}.");
    }
}
