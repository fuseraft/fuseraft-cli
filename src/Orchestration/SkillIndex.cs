using Microsoft.Data.Sqlite;
using fuseraft.Core;

namespace fuseraft.Orchestration;

/// <summary>
/// SQLite FTS5-backed index of skills written to the skills library.
/// Supports fast full-text search across skill content so sessions can find
/// relevant skills by task description at startup.
///
/// <para>
/// The index lives at <c>~/.fuseraft/skills/index.db</c> by default (configurable
/// via <see cref="fuseraft.Core.Models.SkillCurationConfig.IndexPath"/>).
/// It is updated by <see cref="SkillCurator"/> each time a new or updated skill
/// is written to the library.
/// </para>
/// </summary>
public sealed class SkillIndex(string? dbPath = null) : IAsyncDisposable
{
    private readonly string _path = dbPath ?? FuseraftPaths.GlobalSkillsIndex;
    private SqliteConnection? _conn;

    // Schema

    private const string CreateFtsSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS skills_fts USING fts5(
            slug,
            description,
            content,
            path UNINDEXED,
            tokenize = 'porter ascii'
        );
        """;

    // Public API

    /// <summary>Creates the FTS5 table if it does not already exist.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateFtsSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Adds or replaces the skill with the given <paramref name="slug"/> in the index.
    /// </summary>
    public async Task IndexAsync(
        string slug,
        string skillPath,
        string content,
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        // Extract description from YAML frontmatter if present
        var description = ExtractDescription(content);

        await using var tx = await conn.BeginTransactionAsync(ct) as SqliteTransaction;

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM skills_fts WHERE slug = $slug";
            del.Parameters.AddWithValue("$slug", slug);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO skills_fts(slug, description, content, path)
                VALUES ($slug, $description, $content, $path)
                """;
            ins.Parameters.AddWithValue("$slug",        slug);
            ins.Parameters.AddWithValue("$description", description);
            ins.Parameters.AddWithValue("$content",     content);
            ins.Parameters.AddWithValue("$path",        skillPath);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx!.CommitAsync(ct);
    }

    /// <summary>
    /// Searches the index for skills matching <paramref name="query"/> and returns
    /// up to <paramref name="topN"/> results ranked by FTS5 score.
    /// Returns an empty list when no index exists or the query produces no matches.
    /// </summary>
    public async Task<IReadOnlyList<SkillMatch>> SearchAsync(
        string query,
        int topN = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !File.Exists(_path))
            return [];

        var conn = await GetConnectionAsync(ct);

        // Sanitize query for FTS5: wrap in quotes for phrase search, strip special chars
        var ftsQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery)) return [];

        var results = new List<SkillMatch>();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT slug, path, description,
                       snippet(skills_fts, 2, '**', '**', '…', 12) AS excerpt
                FROM skills_fts
                WHERE skills_fts MATCH $query
                ORDER BY rank
                LIMIT $topN
                """;
            cmd.Parameters.AddWithValue("$query", ftsQuery);
            cmd.Parameters.AddWithValue("$topN",  topN);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new SkillMatch(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
            }
        }
        catch (SqliteException)
        {
            // Query parse errors (e.g. special chars) return empty rather than crash
            return [];
        }

        return results;
    }

    /// <summary>Removes the skill with the given <paramref name="slug"/> from the index.</summary>
    public async Task RemoveAsync(string slug, CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return;

        var conn = await GetConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skills_fts WHERE slug = $slug";
        cmd.Parameters.AddWithValue("$slug", slug);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Scans <paramref name="dir"/> for <c>SKILL.md</c> files and indexes any that are
    /// missing or out of date. Useful for bootstrapping the index from an existing library.
    /// </summary>
    public async Task IndexDirectoryAsync(string dir, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir)) return;

        await EnsureSchemaAsync(ct);

        foreach (var skillMd in Directory.EnumerateFiles(dir, "SKILL.md", SearchOption.AllDirectories))
        {
            var slug = Path.GetFileName(Path.GetDirectoryName(skillMd)) ?? Path.GetFileNameWithoutExtension(skillMd);
            var content = await File.ReadAllTextAsync(skillMd, ct);
            await IndexAsync(slug, skillMd, content, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.DisposeAsync();
            _conn = null;
        }
    }

    // Internals

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_conn is not null) return _conn;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        SqliteConnection conn;
        try
        {
            conn = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        }
        catch (Exception ex) when (IsMissingNativeLib(ex))
        {
            throw new InvalidOperationException(
                "SQLite native library (e_sqlite3) could not be loaded. " +
                "Re-install fuseraft to get the updated binary with the embedded SQLite library.", ex);
        }

        _conn = conn;
        await _conn.OpenAsync(ct);

        // WAL mode for concurrent read access alongside writes
        await using var wal = _conn.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        await wal.ExecuteNonQueryAsync(ct);

        await EnsureSchemaAsync(ct);
        return _conn;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> (or any inner exception) is a
    /// <see cref="DllNotFoundException"/> for the SQLite native library, which happens
    /// when the binary was installed without the embedded <c>e_sqlite3</c> native library.
    /// </summary>
    private static bool IsMissingNativeLib(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is DllNotFoundException dll &&
                (dll.Message.Contains("e_sqlite3", StringComparison.OrdinalIgnoreCase) ||
                 dll.Message.Contains("sqlite",    StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    private static string ExtractDescription(string skillContent)
    {
        // Pull description from YAML frontmatter: `description: "..."` or `description: ...`
        foreach (var line in skillContent.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["description:".Length..].Trim();
                return value.Trim('"').Trim('\'').ToString();
            }
        }
        return string.Empty;
    }

    private static string SanitizeFtsQuery(string raw)
    {
        // FTS5 special characters that break the parser: " ^ * : ( ) OR AND NOT
        // Strategy: take individual words and join them with implicit AND (FTS5 default)
        var words = raw
            .Split([' ', '\t', '\n', '\r', '"', '\'', '(', ')', '*', '^', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .Take(10);

        return string.Join(" ", words);
    }
}

/// <summary>A skill matched by FTS5 search.</summary>
public record SkillMatch(
    string Slug,
    string Path,
    string Description,
    string Excerpt);
