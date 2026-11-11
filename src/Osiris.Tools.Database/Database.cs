using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Osiris.Tools.Database;

public sealed record DbColumnInput(
    [property: Description("Lowercase letters, digits and _")] string Name,
    [property: Description("text, integer, real, boolean, date (YYYY-MM-DD), datetime (ISO 8601) or json")] string Type,
    bool Required = false,
    [property: Description("The value can't repeat (an e-mail, say)")] bool Unique = false,
    [property: Description("A table whose row id the column holds (type integer)")] string? References = null);

public sealed record DbColumn(string Name, string Type, bool Required, bool Unique, string? References, bool System);

public sealed record DbTable(string Name, string Description, long Rows, DbColumn[] Columns);

public sealed record DbRename(string From, string To);

/// <summary>A refused request; its message is safe to show to the user and the model.</summary>
public sealed class DataException(string message) : Exception(message);

/// <summary>
/// The workspace's database for the agent's data (contacts, deals…): SQLite in <c>.osiris/database.db</c>. Every table
/// gets <c>id</c>, <c>created_at</c> and <c>updated_at</c>; the rest the agent defines. Writes are structured (no SQL
/// from outside); free-form SQL is one read-only SELECT, stopped after <see cref="QueryTimeout"/>.
/// </summary>
public sealed partial class Database(IWorkspace workspace) : IToolProvider
{
    /// <summary>The constructor Osiris uses when the package is loaded.</summary>
    public Database(OsirisContext context) : this(context.Workspace) { }

    IEnumerable<AITool> IToolProvider.GetTools() => Tools();
    string IToolProvider.GetPrompt() => Prompt();

    public const int MaxColumns = 100;
    public const int MaxRowsPerWrite = 100;
    public const int QueryRows = 200;
    const int MaxText = 100_000;
    const string Meta = "_tables";
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    static readonly string[] SystemColumns = ["id", "created_at", "updated_at"];
    static readonly Dictionary<string, string> Declared = new()
    {
        ["text"] = "TEXT", ["integer"] = "INTEGER", ["real"] = "REAL", ["boolean"] = "BOOLEAN", ["date"] = "DATE", ["datetime"] = "DATETIME", ["json"] = "JSON",
    };

    readonly string file = workspace.DataOf("database.db");
    // Rows go to the model: no indentation to pay tokens for.
    static readonly JsonSerializerOptions Compact = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    // ---- tools ----

    public IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create((string? table = null) => Answer(() => table is null ? new { tables = Tables() } : (object)new { table = Table(table) }), "db_schema",
            "Lists the workspace database's tables with their columns and row counts (or one table). Use it before writing to a table you don't know."),
        AIFunctionFactory.Create((string name, DbColumnInput[] columns, string? description = null) => Answer(() => new { table = CreateTable(name, description ?? "", columns) }), "db_create_table",
            "Creates a table in the workspace database. Every table has id, created_at and updated_at by itself. Create tables as a skill describes " +
            "or when the user asks; check db_schema first whether it exists."),
        AIFunctionFactory.Create((string table, string? description = null, string? rename = null, DbColumnInput[]? add_columns = null, DbRename[]? rename_columns = null, string[]? drop_columns = null) =>
            Answer(() => new { table = AlterTable(table, description, rename, add_columns ?? [], rename_columns ?? [], drop_columns ?? []) }), "db_alter_table",
            "Changes a table: adds columns (never required ones), renames columns or the table, changes its description, drops columns " +
            "(their data is lost: only on the user's explicit instruction)."),
        AIFunctionFactory.Create((string table) => Answer(() => { DropTable(table); return new { dropped = table }; }), "db_drop_table",
            "Deletes a whole table with its data: only on the user's explicit instruction."),
        AIFunctionFactory.Create((string table, Dictionary<string, JsonElement>[] rows) => Answer(() => new { ids = Insert(table, rows) }), "db_insert",
            $"Inserts 1 to {MaxRowsPerWrite} rows into a table at once (all or none); returns their ids. A row maps column → value."),
        AIFunctionFactory.Create((string table, Dictionary<string, JsonElement> where, Dictionary<string, JsonElement> values) => Answer(() => new { updated = Update(table, where, values) }), "db_update",
            """Changes column values of the rows matching where (column → value, all must hold; null = empty; a list = any of them, e.g. {"id": [1, 2]}); returns how many."""),
        AIFunctionFactory.Create((string table, Dictionary<string, JsonElement> where) => Answer(() => new { deleted = Delete(table, where) }), "db_delete",
            "Deletes the rows matching where (as in db_update), only on the user's instruction; returns how many."),
        AIFunctionFactory.Create((string sql, CancellationToken ct) => Answer(() => Query(sql, ct)), "db_query",
            $"Reads data with one SQLite SELECT (JOIN, GROUP BY, WITH too). Returns at most {QueryRows} rows, so filter and sort big tables. " +
            "Search text case-insensitively with LIKE, JSON columns with json_extract. Data changes only with db_insert, db_update and db_delete."),
    ];

    /// <summary>A refusal the model can fix goes back as an answer; anything else fails the tool call.</summary>
    static string Answer(Func<object> action)
    {
        try { return JsonSerializer.Serialize(new { success = true, result = action() }, Compact); }
        catch (DataException e) { return JsonSerializer.Serialize(new { success = false, error = e.Message }); }
    }

    public string Prompt()
    {
        if (!File.Exists(file)) return DatabaseIntro + "(no tables yet)";
        var tables = Tables();
        return DatabaseIntro + (tables.Count == 0 ? "(no tables yet)"
            : string.Join('\n', tables.Select(t => $"- {t.Name} ({t.Rows} rows){(t.Description.Length > 0 ? $": {t.Description}" : "")}")));
    }

    const string DatabaseIntro = """
        # DATABASE
        The workspace has a database for structured data (contacts, deals…). Learn a table's columns with db_schema, read with db_query
        (SELECT), write with db_insert, db_update and db_delete. Keep tables as the skills describing them say; create a new one with care.

        """;

    // ---- tables ----

    public IReadOnlyList<DbTable> Tables()
    {
        using var db = Open();
        return [.. Names(db).Select(n => Describe(db, n))];
    }

    public DbTable Table(string name)
    {
        using var db = Open();
        return Describe(db, Existing(db, name));
    }

    public DbTable CreateTable(string name, string description, DbColumnInput[] columns)
    {
        using var db = Open();
        CheckName(name, "table");
        var names = Names(db);
        if (names.Contains(name)) throw new DataException($"The table {name} exists already.");
        if (columns.Length is 0 or > MaxColumns) throw new DataException($"A table has 1 to {MaxColumns} columns.");
        if (columns.Select(c => c.Name).Distinct().Count() != columns.Length) throw new DataException("Duplicate column names.");
        foreach (var c in columns) CheckColumn(c, [.. names, name]);
        var defs = columns.Select(c => $"{Q(c.Name)} {Declared[c.Type]}{(c.Required ? " NOT NULL" : "")}{Reference(c)}");
        Atomic(db, () =>
        {
            Run(db, $"""CREATE TABLE {Q(name)} ("id" INTEGER PRIMARY KEY, {string.Join(", ", defs)}, "created_at" DATETIME NOT NULL, "updated_at" DATETIME NOT NULL)""");
            foreach (var c in columns.Where(c => c.Unique)) AddUnique(db, name, c.Name);
            SetDescription(db, name, description);
        });
        return Describe(db, name);
    }

    public DbTable AlterTable(string name, string? description, string? rename, DbColumnInput[] addColumns, DbRename[] renameColumns, string[] dropColumns)
    {
        using var db = Open();
        var table = Describe(db, Existing(db, name));
        bool Has(string c) => table.Columns.Any(x => x.Name == c);
        void Own(string c)
        {
            if (SystemColumns.Contains(c)) throw new DataException($"The column {c} is automatic and can't change.");
            if (!Has(c)) throw new DataException($"The table {name} has no column {c}.");
        }

        Atomic(db, () =>
        {
            foreach (var c in dropColumns)
            {
                Own(c);
                foreach (var index in UniqueIndexes(db, name).Where(u => u.Column == c)) Run(db, $"DROP INDEX {Q(index.Index)}");
                Run(db, $"ALTER TABLE {Q(name)} DROP COLUMN {Q(c)}");
                table = Describe(db, name);
            }
            foreach (var (from, to) in renameColumns)
            {
                Own(from);
                CheckName(to, "column");
                if (SystemColumns.Contains(to) || Has(to)) throw new DataException($"The column {to} exists already.");
                Run(db, $"ALTER TABLE {Q(name)} RENAME COLUMN {Q(from)} TO {Q(to)}");
                table = Describe(db, name);
            }
            if (table.Columns.Length + addColumns.Length > MaxColumns + SystemColumns.Length) throw new DataException($"A table has at most {MaxColumns} columns.");
            var names = Names(db);
            foreach (var c in addColumns)
            {
                CheckColumn(c, names);
                if (Has(c.Name)) throw new DataException($"The column {c.Name} exists already.");
                // SQLite adds a NOT NULL column only with a default, which existing rows have none of.
                if (c.Required) throw new DataException($"A new column ({c.Name}) can't be required: the existing rows have no value for it.");
                Run(db, $"ALTER TABLE {Q(name)} ADD COLUMN {Q(c.Name)} {Declared[c.Type]}{Reference(c)}");
                if (c.Unique) AddUnique(db, name, c.Name);
                table = Describe(db, name);
            }
            if (rename is not null && rename != name)
            {
                CheckName(rename, "table");
                if (names.Contains(rename)) throw new DataException($"The table {rename} exists already.");
                Run(db, $"ALTER TABLE {Q(name)} RENAME TO {Q(rename)}");
                Run(db, $"UPDATE {Meta} SET name = $new WHERE name = $old", ("$new", rename), ("$old", name));
                name = rename;
            }
            if (description is not null) SetDescription(db, name, description);
        });
        return Describe(db, name);
    }

    public void DropTable(string name)
    {
        using var db = Open();
        Existing(db, name);
        Atomic(db, () =>
        {
            Run(db, $"DROP TABLE {Q(name)}");
            Run(db, $"DELETE FROM {Meta} WHERE name = $n", ("$n", name));
        });
    }

    // ---- rows ----

    /// <summary>Adds rows in one statement (all or none); returns their ids.</summary>
    public long[] Insert(string name, Dictionary<string, JsonElement>[] rows)
    {
        using var db = Open();
        var table = Describe(db, Existing(db, name));
        if (rows.Length is 0 or > MaxRowsPerWrite) throw new DataException($"Write 1 to {MaxRowsPerWrite} rows at once.");
        var columns = table.Columns.Where(c => !c.System).ToArray();
        foreach (var row in rows) CheckKeys(table, row.Keys);
        var now = DateTime.UtcNow.ToString("O");
        var args = new List<(string, object?)>();
        var tuples = rows.Select((row, r) =>
        {
            var marks = columns.Select((c, i) =>
            {
                args.Add(($"$v{r}_{i}", Encode(c, row.TryGetValue(c.Name, out var v) ? v : default)));
                return $"$v{r}_{i}";
            }).Append("$now").Append("$now");
            return $"({string.Join(", ", marks)})";
        }).ToList();
        args.Add(("$now", now));
        var sql = $"""INSERT INTO {Q(name)} ({string.Join(", ", columns.Select(c => Q(c.Name)).Append("\"created_at\"").Append("\"updated_at\""))}) VALUES {string.Join(", ", tuples)} RETURNING id""";
        using var cmd = Command(db, sql, [.. args]);
        var ids = new List<long>();
        Friendly(() => { using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetInt64(0)); });
        return [.. ids];
    }

    public int Update(string name, Dictionary<string, JsonElement> where, Dictionary<string, JsonElement> values)
    {
        using var db = Open();
        var table = Describe(db, Existing(db, name));
        CheckKeys(table, values.Keys);
        if (values.Count == 0) throw new DataException("Nothing to change.");
        var (filter, args) = Where(table, where);
        var set = values.Keys.Select((k, i) => (Sql: $"{Q(k)} = $s{i}", Arg: ($"$s{i}", Encode(table.Columns.First(c => c.Name == k), values[k])))).ToList();
        return Run(db, $"""UPDATE {Q(name)} SET {string.Join(", ", set.Select(s => s.Sql))}, "updated_at" = $now WHERE {filter}""",
            [.. set.Select(s => s.Arg), ("$now", DateTime.UtcNow.ToString("O")), .. args]);
    }

    public int Delete(string name, Dictionary<string, JsonElement> where)
    {
        using var db = Open();
        var table = Describe(db, Existing(db, name));
        var (filter, args) = Where(table, where);
        return Run(db, $"DELETE FROM {Q(name)} WHERE {filter}", args);
    }

    public sealed record QueryResult(string[] Columns, object?[][] Rows, bool Truncated);

    /// <summary>One read-only SELECT on a read-only connection, interrupted after <see cref="QueryTimeout"/>.</summary>
    public QueryResult Query(string sql, CancellationToken ct = default)
    {
        var statement = ReadOnlySql(sql);
        if (!File.Exists(file)) Open().Dispose(); // the meta table, so a first query sees an empty database, not none
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        Run(db, "PRAGMA query_only = ON");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        using var stop = timeout.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        using var cmd = Command(db, $"SELECT * FROM (\n{statement}\n) LIMIT {QueryRows + 1}");
        try
        {
            using var r = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
            var rows = new List<object?[]>();
            while (r.Read())
                rows.Add([.. Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i) is byte[] b ? $"<binary data, {b.Length} B>" : r.GetValue(i))]);
            return new QueryResult(columns, [.. rows.Take(QueryRows)], rows.Count > QueryRows);
        }
        catch (SqliteException e) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new DataException($"The query took longer than {QueryTimeout.TotalSeconds} s: {e.SqliteErrorCode}.");
        }
        catch (SqliteException e)
        {
            throw new DataException($"SQLite: {e.Message.Replace("SQLite Error 1: ", "")}");
        }
    }

    /// <summary>Strings, quoted names and comments, which may hold anything.</summary>
    [GeneratedRegex(@"--[^\n]*|/\*[\s\S]*?\*/|'(?:[^']|'')*'|""(?:[^""]|"""")*""|`[^`]*`|\[[^\]]*\]")]
    private static partial Regex Literals();

    [GeneratedRegex(@"\b(attach|detach|pragma|vacuum|load_extension)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    /// <summary>
    /// Checks a query is one SELECT (or WITH/VALUES) and returns it without trailing semicolons. It runs on a query-only
    /// connection, which refuses writes; ATTACH (which query_only allows) and PRAGMA are refused here.
    /// </summary>
    public static string ReadOnlySql(string sql)
    {
        var bare = Literals().Replace(sql, m => new string(' ', m.Length));
        if (bare.IndexOfAny(['\'', '"', '`']) >= 0 || bare.Contains("/*")) throw new DataException("The query has an unterminated string or comment.");
        var end = Regex.Match(bare, @";[\s;]*$") is { Success: true } m ? m.Index : bare.Length;
        var body = bare[..end];
        if (body.Contains(';')) throw new DataException("One statement only.");
        if (!Regex.IsMatch(body, @"^\s*(select|with|values)\b", RegexOptions.IgnoreCase)) throw new DataException("Only SELECT queries; data changes with db_insert, db_update and db_delete.");
        if (Forbidden().Match(body) is { Success: true } bad) throw new DataException($"{bad.Value.ToUpperInvariant()} isn't allowed.");
        return sql[..end];
    }

    // ---- inside ----

    SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        Run(db, $"CREATE TABLE IF NOT EXISTS {Meta} (name TEXT PRIMARY KEY, description TEXT NOT NULL DEFAULT '')");
        return db;
    }

    /// <summary>Names are checked against <see cref="NameRule"/> before they get here.</summary>
    static string Q(string name) => $"\"{name}\"";

    static void CheckName(string name, string what)
    {
        if (!NameRule().IsMatch(name)) throw new DataException($"Invalid {what} name \"{name}\": lowercase letters, digits and _, starting with a letter.");
    }

    static List<string> Names(SqliteConnection db)
    {
        using var cmd = Command(db, $"SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name <> '{Meta}' ORDER BY name");
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    static string Existing(SqliteConnection db, string name) =>
        Names(db).Contains(name) ? name : throw new DataException($"There is no table {name}.");

    static DbTable Describe(SqliteConnection db, string name)
    {
        var refs = Rows(db, "SELECT \"from\", \"table\" FROM pragma_foreign_key_list($t)", ("$t", name)).ToDictionary(r => (string)r[0]!, r => (string)r[1]!);
        var unique = UniqueIndexes(db, name).Select(u => u.Column).ToHashSet();
        var columns = Rows(db, "SELECT name, type, \"notnull\" FROM pragma_table_info($t) ORDER BY cid", ("$t", name)).Select(r =>
        {
            var column = (string)r[0]!;
            var system = SystemColumns.Contains(column);
            var type = column == "id" ? "integer" : Declared.FirstOrDefault(d => d.Value.Equals((string)r[1]!, StringComparison.OrdinalIgnoreCase)).Key ?? "text";
            return new DbColumn(column, type, system || (long)r[2]! == 1, column == "id" || unique.Contains(column), refs.GetValueOrDefault(column), system);
        }).ToArray();
        var count = (long)Rows(db, $"SELECT COUNT(*) FROM {Q(name)}")[0][0]!;
        var description = Rows(db, $"SELECT description FROM {Meta} WHERE name = $t", ("$t", name)).FirstOrDefault()?[0] as string ?? "";
        return new DbTable(name, description, count, columns);
    }

    static List<(string Index, string Column)> UniqueIndexes(SqliteConnection db, string table) =>
        [.. Rows(db, """
            SELECT il.name, (SELECT ii.name FROM pragma_index_info(il.name) ii) FROM pragma_index_list($t) il
            WHERE il."unique" = 1 AND (SELECT COUNT(*) FROM pragma_index_info(il.name)) = 1
            """, ("$t", table)).Select(r => ((string)r[0]!, (string)r[1]!))];

    static void SetDescription(SqliteConnection db, string name, string description) =>
        Run(db, $"INSERT INTO {Meta} (name, description) VALUES ($n, $d) ON CONFLICT (name) DO UPDATE SET description = excluded.description",
            ("$n", name), ("$d", description.Trim()[..Math.Min(description.Trim().Length, 500)]));

    static void AddUnique(SqliteConnection db, string table, string column)
    {
        var index = $"{table}__{column}__unique";
        try { Run(db, $"CREATE UNIQUE INDEX {Q(index[..Math.Min(index.Length, 120)])} ON {Q(table)} ({Q(column)})"); }
        catch (DataException) { throw new DataException($"The values of {column} repeat already, so it can't be unique."); }
    }

    static string Reference(DbColumnInput c) =>
        c.References is null ? "" : $" REFERENCES {Q(c.References)}(\"id\") ON DELETE {(c.Required ? "RESTRICT" : "SET NULL")}";

    static void CheckColumn(DbColumnInput c, IReadOnlyCollection<string> tables)
    {
        CheckName(c.Name, "column");
        if (SystemColumns.Contains(c.Name)) throw new DataException($"The column {c.Name} is automatic.");
        if (!Declared.ContainsKey(c.Type)) throw new DataException($"Unknown type {c.Type}; use {string.Join(", ", Declared.Keys)}.");
        if (c.References is not null)
        {
            if (!tables.Contains(c.References)) throw new DataException($"There is no table {c.References} to refer to.");
            if (c.Type != "integer") throw new DataException($"The column {c.Name} refers to a table, so its type is integer.");
        }
    }

    static void CheckKeys(DbTable table, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var col = table.Columns.FirstOrDefault(c => c.Name == key)
                ?? throw new DataException($"The table {table.Name} has no column {key} (it has {string.Join(", ", table.Columns.Select(c => c.Name))}).");
            if (col.System) throw new DataException($"The column {key} is filled automatically.");
        }
    }

    static (string Sql, (string, object?)[] Args) Where(DbTable table, Dictionary<string, JsonElement> where)
    {
        if (where.Count == 0) throw new DataException("Say which rows (where).");
        var parts = new List<string>();
        var args = new List<(string, object?)>();
        foreach (var (key, v) in where)
        {
            var col = table.Columns.FirstOrDefault(c => c.Name == key) ?? throw new DataException($"The table {table.Name} has no column {key}.");
            var strict = col with { Required = true };
            if (v.ValueKind == JsonValueKind.Null) parts.Add($"{Q(key)} IS NULL");
            else if (v.ValueKind == JsonValueKind.Array)
            {
                if (v.GetArrayLength() is 0 or > 1000) throw new DataException($"The list for {key} has 1 to 1000 values.");
                var marks = v.EnumerateArray().Select(x => { var p = $"$w{args.Count}"; args.Add((p, Encode(strict, x))); return p; }).ToList();
                parts.Add($"{Q(key)} IN ({string.Join(", ", marks)})");
            }
            else
            {
                var p = $"$w{args.Count}";
                args.Add((p, Encode(strict, v)));
                parts.Add($"{Q(key)} = {p}");
            }
        }
        return (string.Join(" AND ", parts), [.. args]);
    }

    /// <summary>A value for a column as SQLite stores it; null for none (refused when the column is required).</summary>
    static object? Encode(DbColumn col, JsonElement v)
    {
        DataException Bad(string expected) => new($"{col.Name} needs {expected}, not {(v.ValueKind == JsonValueKind.Undefined ? "nothing" : v.GetRawText()[..Math.Min(80, v.GetRawText().Length)])}.");
        if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || v.ValueKind == JsonValueKind.String && v.GetString() == "")
            return col.Required ? throw new DataException($"{col.Name} is required.") : null;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : null;
        switch (col.Type)
        {
            case "text":
                var text = s ?? (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : throw Bad("text"));
                return text.Length <= MaxText ? text : throw new DataException($"{col.Name} has more than {MaxText} characters.");
            case "integer":
                return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i
                    : s is not null && long.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var si) ? si : throw Bad("an integer");
            case "real":
                return v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                    : s is not null && double.TryParse(s.Trim().Replace(',', '.'), CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : throw Bad("a number");
            case "boolean":
                return v.ValueKind switch
                {
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    JsonValueKind.Number when v.TryGetInt64(out var b) && b is 0 or 1 => b,
                    _ => s switch { "true" => 1L, "false" => 0L, _ => throw Bad("true or false") },
                };
            case "date":
                return s is not null && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? s : throw Bad("a date YYYY-MM-DD");
            case "datetime":
                return s is not null && DateTimeRegex().IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) ? s.Replace(' ', 'T') : throw Bad("an ISO 8601 date and time");
            default: // json
                if (s is null) return v.GetRawText();
                try { using var _ = JsonDocument.Parse(s); return s; }
                catch (JsonException) { return JsonSerializer.Serialize(s); }
        }
    }

    /// <summary>All or nothing. Plain BEGIN/COMMIT: the connection's own transactions would need every command to carry them.</summary>
    static void Atomic(SqliteConnection db, Action action)
    {
        Run(db, "BEGIN");
        try
        {
            action();
            Run(db, "COMMIT");
        }
        catch
        {
            Run(db, "ROLLBACK");
            throw;
        }
    }

    static int Run(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(db, sql, args);
        var n = 0;
        Friendly(() => n = cmd.ExecuteNonQuery());
        return n;
    }

    static List<object?[]> Rows(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(db, sql, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (r.Read()) rows.Add([.. Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i))]);
        return rows;
    }

    static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    /// <summary>SQLite's constraint errors in words the user understands.</summary>
    static void Friendly(Action action)
    {
        try { action(); }
        catch (SqliteException e)
        {
            var message = e.Message;
            if (Regex.Match(message, @"UNIQUE constraint failed: \w+\.(\w+)") is { Success: true } u) throw new DataException($"The value of {u.Groups[1].Value} exists already, and it must be unique.");
            if (message.Contains("FOREIGN KEY constraint failed")) throw new DataException("A row refers to one that doesn't exist (or is still referred to).");
            if (Regex.Match(message, @"NOT NULL constraint failed: \w+\.(\w+)") is { Success: true } nn) throw new DataException($"{nn.Groups[1].Value} is required.");
            throw new DataException($"SQLite: {message}");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex DateTimeRegex();
}
