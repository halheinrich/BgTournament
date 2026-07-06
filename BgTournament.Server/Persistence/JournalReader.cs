using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The one journal read policy, shared by startup rehydration and the audit
/// read surface: a journal that cannot be trusted end-to-end is read to its
/// trusted prefix, never poisoned. A <em>final</em> line that fails to parse
/// is a torn tail (a crash mid-append) — dropped with a warning; a failure on
/// any earlier line is corruption — reading stops there, loudly, and the
/// caller gets a corruption detail to surface. Never trust events past a
/// corrupt line, even a well-formed terminal.
/// </summary>
internal static class JournalReader
{
    /// <summary>
    /// Read one journal into its trusted event prefix under the damage
    /// policy. Null when the file cannot be read at all (no journal, no
    /// access) — the caller decides what that absence means.
    /// </summary>
    public static async Task<(IReadOnlyList<TEvent> Events, string? CorruptionDetail)?>
        ReadTrustedEventsAsync<TEvent>(
            IJournalStore store, JournalKind kind, string id, Func<string, TEvent> deserialize,
            ILogger logger)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(deserialize);
        ArgumentNullException.ThrowIfNull(logger);

        List<string> lines = [];
        try
        {
            using var reader = new StreamReader(store.OpenJournal(kind, id), Encoding.UTF8);
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                ex, "Could not read the {Kind} journal '{Id}'; skipping it.", kind, id);
            return null;
        }

        var events = new List<TEvent>(lines.Count);
        string? corruptionDetail = null;
        for (int i = 0; i < lines.Count; i++)
        {
            try
            {
                events.Add(deserialize(lines[i]));
            }
            catch (JsonException ex)
            {
                if (i == lines.Count - 1)
                {
                    // A crash mid-append leaves a torn final line; dropping it
                    // is the settled policy — the events before it are whole.
                    logger.LogWarning(
                        ex,
                        "The {Kind} journal '{Id}' has a torn final line ({Line}); dropped it and "
                            + "folded the rest.",
                        kind, id, i + 1);
                }
                else
                {
                    corruptionDetail =
                        $"The journal is corrupt at line {i + 1}; this record reflects only the "
                            + "events before the corruption.";
                    logger.LogError(
                        ex,
                        "The {Kind} journal '{Id}' is corrupt at line {Line} (not the tail); folding "
                            + "only the events before it.",
                        kind, id, i + 1);
                }

                break;
            }
        }

        return (events, corruptionDetail);
    }
}
