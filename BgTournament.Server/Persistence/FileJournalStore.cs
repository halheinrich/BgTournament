using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The file-backed journal store: one <c>.jsonl</c> file per journal under
/// <see cref="PersistenceOptions.DataDirectory"/> — <c>matches/&lt;id&gt;.jsonl</c>,
/// <c>tournaments/&lt;id&gt;.jsonl</c>, <c>server/&lt;id&gt;.jsonl</c>
/// (one segment per server session), and <c>roster/&lt;id&gt;.jsonl</c>
/// (one segment per roster-mutating session). Ids are server-generated
/// GUID-"N" values, so they are filesystem-safe by construction.
/// </summary>
internal sealed class FileJournalStore : IJournalStore
{
    private readonly string _root;

    public FileJournalStore(IOptions<PersistenceOptions> options, IHostEnvironment environment)
    {
        _root = Path.GetFullPath(options.Value.DataDirectory, environment.ContentRootPath);
    }

    /// <inheritdoc/>
    public Stream CreateJournal(JournalKind kind, string id)
    {
        string directory = DirectoryFor(kind);
        Directory.CreateDirectory(directory);

        // CreateNew: a journal is append-only and written exactly once — an
        // existing file with this id is a bug, never something to overwrite.
        // Readers may share (a running journal is inspectable).
        return new FileStream(
            PathFor(directory, id), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ListJournalIds(JournalKind kind)
    {
        string directory = DirectoryFor(kind);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToArray();
    }

    /// <inheritdoc/>
    public Stream OpenJournal(JournalKind kind, string id) =>
        new FileStream(
            PathFor(DirectoryFor(kind), id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private string DirectoryFor(JournalKind kind) =>
        Path.Combine(_root, kind switch
        {
            JournalKind.Match => "matches",
            JournalKind.Tournament => "tournaments",
            JournalKind.Server => "server",
            JournalKind.Roster => "roster",
            _ => throw new InvalidOperationException($"Unhandled JournalKind value: {kind}."),
        });

    private static string PathFor(string directory, string id) =>
        Path.Combine(directory, id + ".jsonl");
}
