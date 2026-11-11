using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Osiris.Core;

public sealed record KnowledgeHit(string Path, string Text, float Score);

/// <summary>
/// The knowledge base: Markdown files under <c>knowledge/</c> in the workspace. Their chunks' embeddings are kept in
/// SQLite with the sqlite-vec extension (<c>.osiris/knowledge.db</c>) and brought in step with the files before every
/// search, so a document added, changed or removed (by the agent, the user or a git pull) is searched as it is now.
/// A changed embedding model reindexes all.
/// </summary>
public sealed class Knowledge(Workspace workspace, IEmbeddingGenerator<string, Embedding<float>> embeddings) : IToolProvider
{
    IEnumerable<AITool> IToolProvider.GetTools() => Tools();
    string IToolProvider.GetPrompt() => Prompt();

    public const string Folder = "knowledge";
    const int ChunkChars = 1000;
    const int EmbedBatch = 32;

    readonly string dir = Path.Combine(workspace.Root, Folder);
    readonly string dbFile = workspace.DataOf("knowledge.db");
    string model => embeddings.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? "";
    readonly SemaphoreSlim gate = new(1, 1);

    public string Prompt() => $"""
        # KNOWLEDGE BASE
        The workspace's documents are Markdown files under {Folder}/. Before answering about the workspace's clients, work or
        business, search them with search_knowledge. Add or change knowledge by writing those files; it's searchable right after.
        """;

    public IList<AITool> Tools() => [AIFunctionFactory.Create(SearchKnowledge, "search_knowledge")];

    [Description("Searches the knowledge base (notes, price lists, procedures…) by meaning. Returns the most relevant passages with their file.")]
    public async Task<string> SearchKnowledge([Description("What to look for, in natural language")] string query, CancellationToken ct = default)
    {
        var hits = await SearchAsync(query, 5, ct);
        return hits.Count == 0 ? "Nothing found." : string.Join("\n\n---\n\n", hits.Select((h, i) => $"[{i + 1}] {h.Path}\n{h.Text}"));
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var db = Open();
            await SyncAsync(db, ct);
            if (string.IsNullOrWhiteSpace(query) || !HasChunks(db)) return [];
            var q = (await embeddings.GenerateAsync([query], cancellationToken: ct))[0].Vector;
            using var cmd = Command(db, null, "select path, body, distance from chunks where embedding match $q and k = $k order by distance",
                ("$q", Blob(q)), ("$k", limit));
            using var r = cmd.ExecuteReader();
            var hits = new List<KnowledgeHit>();
            while (r.Read()) hits.Add(new KnowledgeHit(r.GetString(0), r.GetString(1), 1 - (float)r.GetDouble(2)));
            return hits;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Indexes new and changed documents and drops the removed ones.</summary>
    async Task SyncAsync(SqliteConnection db, CancellationToken ct)
    {
        var known = new Dictionary<string, (string Hash, string Model)>();
        using (var cmd = Command(db, null, "select path, hash, model from docs"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) known[r.GetString(0)] = (r.GetString(1), r.GetString(2));

        // Another model's vectors (maybe of another size) don't compare with this one's: start over.
        if (known.Values.Any(k => k.Model != model))
        {
            Exec(db, null, "drop table if exists chunks; delete from docs");
            known.Clear();
        }

        var files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories) : [];
        foreach (var file in files)
        {
            var path = workspace.Relative(file);
            var text = await File.ReadAllTextAsync(file, ct);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            if (known.Remove(path, out var was) && was.Hash == hash) continue;

            var pieces = Split(text);
            var vectors = await EmbedAsync(pieces, ct);
            using var tx = db.BeginTransaction();
            if (vectors.Count > 0)
                Exec(db, tx, $"create virtual table if not exists chunks using vec0(embedding float[{vectors[0].Length}] distance_metric=cosine, +path text, +body text)");
            if (HasChunks(db, tx)) Exec(db, tx, "delete from chunks where path = $p", ("$p", path));
            for (var i = 0; i < pieces.Count; i++)
                Exec(db, tx, "insert into chunks(embedding, path, body) values ($e, $p, $b)", ("$e", Blob(vectors[i])), ("$p", path), ("$b", pieces[i]));
            Exec(db, tx, "insert or replace into docs(path, hash, model) values ($p, $h, $m)", ("$p", path), ("$h", hash), ("$m", model));
            tx.Commit();
        }

        // What's left in `known` has no file any more.
        foreach (var gone in known.Keys)
        {
            using var tx = db.BeginTransaction();
            if (HasChunks(db, tx)) Exec(db, tx, "delete from chunks where path = $p", ("$p", gone));
            Exec(db, tx, "delete from docs where path = $p", ("$p", gone));
            tx.Commit();
        }
    }

    async Task<List<ReadOnlyMemory<float>>> EmbedAsync(List<string> pieces, CancellationToken ct)
    {
        var vectors = new List<ReadOnlyMemory<float>>();
        foreach (var batch in pieces.Chunk(EmbedBatch))
            vectors.AddRange((await embeddings.GenerateAsync(batch, cancellationToken: ct)).Select(e => e.Vector));
        return vectors;
    }

    /// <summary>Paragraphs packed into chunks of up to <see cref="ChunkChars"/>; a longer paragraph is cut.</summary>
    public static List<string> Split(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split("\n\n").Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > ChunkChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            for (var p = paragraph; p.Length > 0; p = p[Math.Min(ChunkChars, p.Length)..])
            {
                if (current.Length > 0) current.Append("\n\n");
                current.Append(p[..Math.Min(ChunkChars, p.Length)]);
                if (p.Length > ChunkChars) { chunks.Add(current.ToString()); current.Clear(); }
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbFile)!);
        // No pooling: every connection loads the extension afresh, and the file isn't held open between runs.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Pooling = false }.ToString());
        db.Open();
        db.EnableExtensions();
        db.LoadExtension("vec0");
        Exec(db, null, "create table if not exists docs(path text primary key, hash text not null, model text not null)");
        return db;
    }

    static bool HasChunks(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = Command(db, tx, "select count(*) from sqlite_master where name = 'chunks'");
        return (long)cmd.ExecuteScalar()! > 0;
    }

    static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = Command(db, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    /// <summary>A vector as sqlite-vec reads it: little-endian float32s.</summary>
    static byte[] Blob(ReadOnlyMemory<float> vector) => MemoryMarshal.AsBytes(vector.Span).ToArray();
}
