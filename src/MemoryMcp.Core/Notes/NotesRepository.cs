using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Thin facade composing <see cref="NotesReader"/> (queries) and <see cref="NotesWriter"/> (commands).
/// Holds no logic — it is a convenience entry point; consumers that need only one side may depend on
/// the reader or writer directly (preferred). The real work lives in those classes and the static
/// <see cref="NoteRowMapper"/> / <see cref="SnippetBuilder"/> leaves.
/// </summary>
public sealed class NotesRepository
{
    private readonly NotesReader _reader;
    private readonly NotesWriter _writer;

    /// <summary>Creates the facade over the given database, schema registry and clock.</summary>
    /// <param name="connectionFactory">Database connection factory.</param>
    /// <param name="registry">Schema registry used for validation and version stamping.</param>
    /// <param name="timeProvider">Clock for timestamps; defaults to the system clock.</param>
    public NotesRepository(ISqliteConnectionFactory connectionFactory, SchemaRegistry registry, TimeProvider? timeProvider = null)
    {
        _reader = new NotesReader(connectionFactory);
        _writer = new NotesWriter(connectionFactory, registry, timeProvider);
    }

    /// <inheritdoc cref="NotesWriter.Upsert"/>
    public UpsertResult Upsert(
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey, string? sourceAgent)
        => _writer.Upsert(domain, type, title, body, payloadJson, tagsJson, dedupKey, sourceAgent);

    /// <inheritdoc cref="NotesWriter.AppendJournal"/>
    public string AppendJournal(string domain, string text, string? title = null, string? tagsJson = null, string? sourceAgent = null)
        => _writer.AppendJournal(domain, text, title, tagsJson, sourceAgent);

    /// <inheritdoc cref="NotesWriter.Link"/>
    public void Link(string fromId, string toId, string rel) => _writer.Link(fromId, toId, rel);

    /// <inheritdoc cref="NotesWriter.Assemble"/>
    public AssembleResult Assemble(
        string domain, string type, string? title, string? body,
        string? payloadJson, string? tagsJson, string? dedupKey,
        IReadOnlyList<AssembleLink>? links, string? sourceAgent)
        => _writer.Assemble(domain, type, title, body, payloadJson, tagsJson, dedupKey, links, sourceAgent);

    /// <inheritdoc cref="NotesWriter.Archive"/>
    public bool Archive(string id) => _writer.Archive(id);

    /// <inheritdoc cref="NotesWriter.Supersede"/>
    public bool Supersede(string oldId, string newId) => _writer.Supersede(oldId, newId);

    /// <inheritdoc cref="NotesWriter.Patch"/>
    public Note? Patch(string id, string? title, string? body, string? payloadJson, string? tagsJson,
        string? expectedUpdatedUtc, string? sourceAgent)
        => _writer.Patch(id, title, body, payloadJson, tagsJson, expectedUpdatedUtc, sourceAgent);

    /// <inheritdoc cref="NotesWriter.Restore"/>
    public bool Restore(string id) => _writer.Restore(id);

    /// <inheritdoc cref="NotesWriter.Unlink"/>
    public int Unlink(string fromId, string toId, string rel) => _writer.Unlink(fromId, toId, rel);

    /// <inheritdoc cref="NotesReader.Links"/>
    public IReadOnlyList<LinkView> Links(string id) => _reader.Links(id);

    /// <inheritdoc cref="NotesReader.Recall"/>
    public RecallResult Recall(
        string? query, string? domain, int limit, IReadOnlyCollection<string>? restrictToDomains,
        bool includeLinks = true, int maxHops = 1)
        => _reader.Recall(query, domain, limit, restrictToDomains, includeLinks, maxHops);

    /// <inheritdoc cref="NotesReader.Related"/>
    public IReadOnlyList<RelatedNote> Related(string id, int limit, IReadOnlyCollection<string>? restrictToDomains)
        => _reader.Related(id, limit, restrictToDomains);

    /// <inheritdoc cref="NotesReader.Recent"/>
    public IReadOnlyList<SearchResult> Recent(string? domain, string? type, int limit, IReadOnlyCollection<string>? restrictToDomains, bool byUsage)
        => _reader.Recent(domain, type, limit, restrictToDomains, byUsage);

    /// <inheritdoc cref="NotesReader.Events"/>
    public IReadOnlyList<NoteEvent> Events(string id, int limit = 50) => _reader.Events(id, limit);

    /// <inheritdoc cref="NotesReader.Event"/>
    public NoteEventDetail? Event(string noteId, string eventId, int? maxChars = null, string? fields = null)
        => _reader.Event(noteId, eventId, maxChars, fields);

    /// <inheritdoc cref="NotesReader.TagCounts"/>
    public IReadOnlyDictionary<string, long> TagCounts() => _reader.TagCounts();

    /// <inheritdoc cref="NotesReader.Get"/>
    public Note? Get(string id) => _reader.Get(id);

    /// <inheritdoc cref="NotesReader.CountByTypeInDomain"/>
    public IReadOnlyDictionary<string, long> CountByTypeInDomain(string domain) => _reader.CountByTypeInDomain(domain);

    /// <inheritdoc cref="NotesReader.GetView"/>
    public NoteView? GetView(string id, bool includeBody = true, int? bodyMaxChars = null)
        => _reader.GetView(id, includeBody, bodyMaxChars);

    /// <inheritdoc cref="NotesReader.ReadBody"/>
    public NoteReadSlice? ReadBody(string id, int offset = 0, int limitChars = NotesReader.DefaultReadChars)
        => _reader.ReadBody(id, offset, limitChars);

    /// <inheritdoc cref="NotesReader.Outline"/>
    public NoteOutline? Outline(string id) => _reader.Outline(id);

    /// <inheritdoc cref="NotesReader.Find"/>
    public NoteFindResult? Find(string id, string query, int contextChars = NotesReader.DefaultContextChars, int limit = NotesReader.DefaultFindMatches)
        => _reader.Find(id, query, contextChars, limit);

    /// <inheritdoc cref="NotesReader.GetByDedupKey"/>
    public Note? GetByDedupKey(string domain, string type, string dedupKey) => _reader.GetByDedupKey(domain, type, dedupKey);

    /// <inheritdoc cref="NotesReader.List"/>
    public IReadOnlyList<Note> List(string domain, string type, int limit = 1000) => _reader.List(domain, type, limit);

    /// <inheritdoc cref="NotesReader.Search"/>
    public SearchPage Search(
        string? query = null, string? domain = null, string? type = null,
        IReadOnlyCollection<string>? tags = null, string status = "active",
        int limit = NotesReader.DefaultLimit, int offset = 0, IReadOnlyCollection<string>? restrictToDomains = null,
        string? filter = null, bool includePayload = false, bool includeLinks = false, string? sort = null)
        => _reader.Search(query, domain, type, tags, status, limit, offset, restrictToDomains, filter, includePayload, includeLinks, sort);
}
