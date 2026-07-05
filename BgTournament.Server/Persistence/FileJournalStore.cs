using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BgTournament.Server.Persistence;

/// <summary>
/// The file-backed journal store: one <c>.jsonl</c> file per journal under
/// <see cref="PersistenceOptions.DataDirectory"/> — <c>matches/&lt;id&gt;.jsonl</c>
/// and <c>tournaments/&lt;id&gt;.jsonl</c>. Ids are the server's GUID-"N"
/// record ids, so they are filesystem-safe by construction.
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
        Path.Combine(_root, kind == JournalKind.Match ? "matches" : "tournaments");

    private static string PathFor(string directory, string id) =>
        Path.Combine(directory, id + ".jsonl");
}
