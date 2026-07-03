using BgTournament.Protocol;

namespace BgTournament.Tests;

/// <summary>
/// Keeps PROTOCOL.md honest: every fenced JSON example in the doc must be a
/// valid protocol message in canonical wire form, byte-for-byte. A DTO or
/// serializer change that alters the wire fails these tests until the doc
/// (and the golden pins) move with it — doc drift fails loudly instead of
/// misleading the next engine author.
/// </summary>
public class ProtocolDocTests
{
    private static string ProtocolDocPath { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PROTOCOL.md"));

    private static IReadOnlyList<string> FencedJsonLines()
    {
        var lines = new List<string>();
        bool inJsonFence = false;
        foreach (var raw in File.ReadAllLines(ProtocolDocPath))
        {
            var line = raw.Trim();
            if (line == "```json")
            {
                inJsonFence = true;
            }
            else if (line == "```")
            {
                inJsonFence = false;
            }
            else if (inJsonFence && line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    [Fact]
    public void ProtocolDoc_Exists()
    {
        Assert.True(File.Exists(ProtocolDocPath), $"PROTOCOL.md not found at '{ProtocolDocPath}'.");
    }

    [Fact]
    public void EveryFencedJsonExample_IsACanonicalWireMessage()
    {
        var examples = FencedJsonLines();

        // Guard against extraction rot: the doc carries a substantial example set.
        Assert.True(
            examples.Count >= 10,
            $"Expected at least 10 fenced JSON examples in PROTOCOL.md, found {examples.Count}.");

        foreach (var example in examples)
        {
            var reserialized = WireProtocol.Serialize(WireProtocol.Deserialize(example));
            Assert.Equal(example, reserialized);
        }
    }
}
