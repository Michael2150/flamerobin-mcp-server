using System.ComponentModel;
using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;
using ModelContextProtocol.Server;

[McpServerToolType]
public class FirebirdTools(Dictionary<string, FbConnectionStringBuilder> dbs)
{
    FbConnection Open(string key)
    {
        if (!dbs.TryGetValue(key, out var csb))
            throw new ArgumentException($"Unknown database '{key}'. Call list_databases first.");
        var conn = new FbConnection(csb.ConnectionString);
        conn.Open();
        return conn;
    }

    List<Dictionary<string, object?>> Rows(FbCommand cmd)
    {
        using var rdr = cmd.ExecuteReader();
        var result = new List<Dictionary<string, object?>>();
        while (rdr.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < rdr.FieldCount; i++)
                row[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
            result.Add(row);
        }
        return result;
    }

    static List<string> FilterNames(List<string> names, string? filter) =>
        filter is null ? names : names.Where(n => Regex.IsMatch(n, filter, RegexOptions.IgnoreCase)).ToList();

    [McpServerTool, Description("List all servers and databases registered in FlameRobin.")]
    public List<object> ListDatabases() =>
        dbs.Select(kv => (object)new {
            key  = kv.Key,
            host = kv.Value.DataSource,
            port = kv.Value.Port,
            path = kv.Value.Database
        }).ToList();

    [McpServerTool, Description(
        "List user tables, views, or both. type: 'tables' | 'views' | 'all' (default). " +
        "filter: optional regex applied to object names (case-insensitive).")]
    public List<string> ListObjects(string database, string type = "all", string? filter = null)
    {
        using var conn = Open(database);
        var where = type.ToLower() switch
        {
            "tables" => "RDB$SYSTEM_FLAG=0 AND RDB$VIEW_BLR IS NULL",
            "views"  => "RDB$SYSTEM_FLAG=0 AND RDB$VIEW_BLR IS NOT NULL",
            _        => "RDB$SYSTEM_FLAG=0"
        };
        using var cmd = new FbCommand(
            $"SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE {where} ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return FilterNames(r, filter);
    }

    [McpServerTool, Description("List all stored procedures. filter: optional regex (case-insensitive).")]
    public List<string> ListProcedures(string database, string? filter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$PROCEDURE_NAME) FROM RDB$PROCEDURES " +
            "WHERE RDB$SYSTEM_FLAG=0 ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return FilterNames(r, filter);
    }

    [McpServerTool, Description(
        "List all user triggers as 'TRIGGER_NAME (on TABLE_NAME)'. " +
        "filter: optional regex applied to the full string (case-insensitive).")]
    public List<string> ListTriggers(string database, string? filter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$TRIGGER_NAME)||' (on '||TRIM(RDB$RELATION_NAME)||')' " +
            "FROM RDB$TRIGGERS WHERE RDB$SYSTEM_FLAG=0 ORDER BY 2,1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return FilterNames(r, filter);
    }

    [McpServerTool, Description("List all generators/sequences with current values. filter: optional regex on name.")]
    public List<object> ListGenerators(string database, string? filter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$GENERATOR_NAME), GEN_ID(RDB$GENERATOR_NAME,0) " +
            "FROM RDB$GENERATORS WHERE RDB$SYSTEM_FLAG=0 ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<object>();
        while (rdr.Read()) r.Add(new { name = rdr.GetString(0), value = rdr.GetValue(1) });
        return filter is null ? r
            : r.Where(x => Regex.IsMatch(((dynamic)x).name, filter, RegexOptions.IgnoreCase)).ToList();
    }

    [McpServerTool, Description("List all roles in the database. filter: optional regex on name.")]
    public List<string> ListRoles(string database, string? filter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$ROLE_NAME) FROM RDB$ROLES ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return FilterNames(r, filter);
    }

    [McpServerTool, Description(
        "Return column definitions for a table or view. " +
        "brief=true returns only name, type, nullable. " +
        "filter: optional regex on column name.")]
    public List<object> DescribeTable(string database, string table, bool brief = false, string? filter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(@"
            SELECT TRIM(rf.RDB$FIELD_NAME)      AS name,
                   TRIM(t.RDB$TYPE_NAME)         AS type,
                   f.RDB$FIELD_LENGTH             AS length,
                   f.RDB$FIELD_PRECISION          AS precision,
                   f.RDB$FIELD_SCALE              AS scale,
                   COALESCE(rf.RDB$NULL_FLAG,0)   AS not_null,
                   rf.RDB$DEFAULT_SOURCE          AS default_src,
                   rf.RDB$DESCRIPTION             AS description
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME=rf.RDB$FIELD_SOURCE
            JOIN RDB$TYPES  t ON t.RDB$TYPE=f.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME='RDB$FIELD_TYPE'
            WHERE rf.RDB$RELATION_NAME=@t ORDER BY rf.RDB$FIELD_POSITION", conn);
        cmd.Parameters.AddWithValue("@t", table.ToUpper());
        using var rdr = cmd.ExecuteReader();
        var result = new List<object>();
        while (rdr.Read())
        {
            var name = rdr.GetString(0);
            if (filter is not null && !Regex.IsMatch(name, filter, RegexOptions.IgnoreCase))
                continue;
            if (brief)
            {
                result.Add(new {
                    name,
                    type     = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    nullable = rdr.GetInt16(5) == 0
                });
            }
            else
            {
                result.Add(new {
                    name,
                    type        = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    length      = rdr.IsDBNull(2) ? (object?)null : rdr.GetValue(2),
                    precision   = rdr.IsDBNull(3) ? (object?)null : rdr.GetValue(3),
                    scale       = rdr.IsDBNull(4) ? (object?)null : rdr.GetValue(4),
                    nullable    = rdr.GetInt16(5) == 0,
                    default_src = rdr.IsDBNull(6) ? null : rdr.GetString(6)?.Trim(),
                    description = rdr.IsDBNull(7) ? null : rdr.GetString(7)?.Trim()
                });
            }
        }
        return result;
    }

    [McpServerTool, Description("Return the PSQL source of a stored procedure.")]
    public string GetProcedureSource(string database, string procedure)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$PROCEDURE_SOURCE FROM RDB$PROCEDURES WHERE RDB$PROCEDURE_NAME=@p", conn);
        cmd.Parameters.AddWithValue("@p", procedure.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"Procedure '{procedure}' not found.";
    }

    [McpServerTool, Description("Return the PSQL source of a trigger.")]
    public string GetTriggerSource(string database, string trigger)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$TRIGGER_SOURCE FROM RDB$TRIGGERS WHERE RDB$TRIGGER_NAME=@t", conn);
        cmd.Parameters.AddWithValue("@t", trigger.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"Trigger '{trigger}' not found.";
    }

    [McpServerTool, Description("Return the source SQL of a view.")]
    public string GetViewSource(string database, string view)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$VIEW_SOURCE FROM RDB$RELATIONS WHERE RDB$RELATION_NAME=@v", conn);
        cmd.Parameters.AddWithValue("@v", view.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"View '{view}' not found.";
    }

    [McpServerTool, Description("Return table constraints (PK, FK, UNIQUE, CHECK).")]
    public List<object> GetTableConstraints(string database, string table)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$CONSTRAINT_NAME), TRIM(RDB$CONSTRAINT_TYPE), TRIM(RDB$INDEX_NAME) " +
            "FROM RDB$RELATION_CONSTRAINTS WHERE RDB$RELATION_NAME=@t ORDER BY 2,1", conn);
        cmd.Parameters.AddWithValue("@t", table.ToUpper());
        using var rdr = cmd.ExecuteReader();
        var r = new List<object>();
        while (rdr.Read())
            r.Add(new {
                constraint = rdr.IsDBNull(0) ? null : rdr.GetString(0),
                type       = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                index      = rdr.IsDBNull(2) ? null : rdr.GetString(2)
            });
        return r;
    }

    [McpServerTool, Description("Return database info: ODS, page size, dialect, etc.")]
    public object GetDatabaseInfo(string database)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT MON$DATABASE_NAME, MON$ODS_MAJOR, MON$ODS_MINOR, " +
            "MON$PAGE_SIZE, MON$PAGES, MON$SQL_DIALECT, MON$SWEEP_INTERVAL FROM MON$DATABASE", conn);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return new { };
        return new {
            path           = rdr.IsDBNull(0) ? null : rdr.GetString(0),
            ods_major      = rdr.GetValue(1),
            ods_minor      = rdr.GetValue(2),
            page_size      = rdr.GetValue(3),
            pages          = rdr.GetValue(4),
            sql_dialect    = rdr.GetValue(5),
            sweep_interval = rdr.GetValue(6)
        };
    }

    [McpServerTool, Description("List active connections to this database.")]
    public List<object> ListActiveConnections(string database)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT MON$ATTACHMENT_ID, TRIM(MON$USER), TRIM(MON$REMOTE_ADDRESS), " +
            "TRIM(MON$REMOTE_PROCESS), MON$TIMESTAMP " +
            "FROM MON$ATTACHMENTS WHERE MON$SYSTEM_FLAG=0 ORDER BY MON$TIMESTAMP", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<object>();
        while (rdr.Read())
            r.Add(new {
                id           = rdr.GetValue(0),
                user         = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                address      = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                process      = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                connected_at = rdr.IsDBNull(4) ? (object?)null : rdr.GetValue(4)
            });
        return r;
    }

    [McpServerTool, Description(
        "Execute a SELECT query. Use Firebird syntax (ROWS n, not LIMIT n). " +
        "columns: optional comma-separated list of column names to include in results.")]
    public List<Dictionary<string, object?>> RunQuery(
        string database, string sql, int maxRows = 500, string? columns = null)
    {
        var include = columns?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var conn = Open(database);
        using var cmd = new FbCommand(sql, conn);
        cmd.FetchSize = maxRows;
        using var rdr = cmd.ExecuteReader();
        var result = new List<Dictionary<string, object?>>();
        while (rdr.Read() && result.Count < maxRows)
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var colName = rdr.GetName(i);
                if (include is not null && !include.Contains(colName)) continue;
                row[colName] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
            }
            result.Add(row);
        }
        return result;
    }

    [McpServerTool, Description(
        "Return foreign key relationships for a table. " +
        "direction: 'out' = FKs this table declares (what it references), " +
        "'in' = FKs other tables declare pointing here (who references this table), " +
        "'all' (default) = both. Each row: from_table, from_column, to_table, to_column, on_update, on_delete.")]
    public List<object> GetForeignKeys(string database, string table, string direction = "all")
    {
        using var conn = Open(database);
        var result = new List<object>();

        if (direction is "out" or "all")
        {
            using var cmd = new FbCommand(@"
                SELECT TRIM(rc.RDB$RELATION_NAME),  TRIM(iseg.RDB$FIELD_NAME),
                       TRIM(rc2.RDB$RELATION_NAME), TRIM(iseg2.RDB$FIELD_NAME),
                       TRIM(refc.RDB$UPDATE_RULE),  TRIM(refc.RDB$DELETE_RULE)
                FROM RDB$RELATION_CONSTRAINTS rc
                JOIN RDB$REF_CONSTRAINTS refc  ON refc.RDB$CONSTRAINT_NAME  = rc.RDB$CONSTRAINT_NAME
                JOIN RDB$RELATION_CONSTRAINTS rc2 ON rc2.RDB$CONSTRAINT_NAME = refc.RDB$CONST_NAME_UQ
                JOIN RDB$INDEX_SEGMENTS iseg   ON iseg.RDB$INDEX_NAME  = rc.RDB$INDEX_NAME
                JOIN RDB$INDEX_SEGMENTS iseg2  ON iseg2.RDB$INDEX_NAME = rc2.RDB$INDEX_NAME
                                               AND iseg2.RDB$FIELD_POSITION = iseg.RDB$FIELD_POSITION
                WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY'
                  AND rc.RDB$RELATION_NAME   = @t
                ORDER BY iseg.RDB$FIELD_POSITION", conn);
            cmd.Parameters.AddWithValue("@t", table.ToUpper());
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add(new {
                    direction   = "out",
                    from_table  = rdr.GetString(0),
                    from_column = rdr.GetString(1),
                    to_table    = rdr.GetString(2),
                    to_column   = rdr.GetString(3),
                    on_update   = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    on_delete   = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                });
        }

        if (direction is "in" or "all")
        {
            using var cmd = new FbCommand(@"
                SELECT TRIM(rc.RDB$RELATION_NAME),  TRIM(iseg.RDB$FIELD_NAME),
                       TRIM(rc2.RDB$RELATION_NAME), TRIM(iseg2.RDB$FIELD_NAME),
                       TRIM(refc.RDB$UPDATE_RULE),  TRIM(refc.RDB$DELETE_RULE)
                FROM RDB$RELATION_CONSTRAINTS rc
                JOIN RDB$REF_CONSTRAINTS refc  ON refc.RDB$CONSTRAINT_NAME  = rc.RDB$CONSTRAINT_NAME
                JOIN RDB$RELATION_CONSTRAINTS rc2 ON rc2.RDB$CONSTRAINT_NAME = refc.RDB$CONST_NAME_UQ
                JOIN RDB$INDEX_SEGMENTS iseg   ON iseg.RDB$INDEX_NAME  = rc.RDB$INDEX_NAME
                JOIN RDB$INDEX_SEGMENTS iseg2  ON iseg2.RDB$INDEX_NAME = rc2.RDB$INDEX_NAME
                                               AND iseg2.RDB$FIELD_POSITION = iseg.RDB$FIELD_POSITION
                WHERE rc.RDB$CONSTRAINT_TYPE  = 'FOREIGN KEY'
                  AND rc2.RDB$RELATION_NAME   = @t
                ORDER BY rc.RDB$RELATION_NAME, iseg.RDB$FIELD_POSITION", conn);
            cmd.Parameters.AddWithValue("@t", table.ToUpper());
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add(new {
                    direction   = "in",
                    from_table  = rdr.GetString(0),
                    from_column = rdr.GetString(1),
                    to_table    = rdr.GetString(2),
                    to_column   = rdr.GetString(3),
                    on_update   = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    on_delete   = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                });
        }

        return result;
    }

    [McpServerTool, Description(
        "Return the execution plan for a SELECT query — shows which indexes Firebird will use. " +
        "Useful for spotting full table scans before running expensive queries.")]
    public string GetExecutionPlan(string database, string sql)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(sql, conn);
        cmd.Prepare();
        return cmd.GetCommandPlan() ?? "No plan returned.";
    }

    [McpServerTool, Description(
        "For a given table, show which columns have indexes and which don't. " +
        "filterColumns: optional comma-separated list of columns you intend to filter/join on — " +
        "only those are checked. Omit to see index coverage for all columns.")]
    public List<object> AnalyzeMissingIndexes(string database, string table, string? filterColumns = null)
    {
        var wantedCols = filterColumns?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpper()).ToHashSet()
            ?? null;

        using var conn = Open(database);

        // All columns on the table
        using var colCmd = new FbCommand(
            "SELECT TRIM(RDB$FIELD_NAME) FROM RDB$RELATION_FIELDS " +
            "WHERE RDB$RELATION_NAME=@t ORDER BY RDB$FIELD_POSITION", conn);
        colCmd.Parameters.AddWithValue("@t", table.ToUpper());
        var allCols = new List<string>();
        using (var rdr = colCmd.ExecuteReader())
            while (rdr.Read()) allCols.Add(rdr.GetString(0));

        // Columns that appear as the leading segment of any active index
        using var idxCmd = new FbCommand(@"
            SELECT TRIM(seg.RDB$FIELD_NAME), TRIM(idx.RDB$INDEX_NAME),
                   COALESCE(idx.RDB$UNIQUE_FLAG, 0)
            FROM RDB$INDEX_SEGMENTS seg
            JOIN RDB$INDICES idx ON idx.RDB$INDEX_NAME = seg.RDB$INDEX_NAME
            WHERE idx.RDB$RELATION_NAME = @t
              AND seg.RDB$FIELD_POSITION = 0
              AND COALESCE(idx.RDB$INDEX_INACTIVE, 0) = 0", conn);
        idxCmd.Parameters.AddWithValue("@t", table.ToUpper());
        var indexed = new Dictionary<string, (string index, bool unique)>(StringComparer.OrdinalIgnoreCase);
        using (var rdr = idxCmd.ExecuteReader())
            while (rdr.Read())
                indexed.TryAdd(rdr.GetString(0), (rdr.GetString(1), rdr.GetInt16(2) != 0));

        var result = new List<object>();
        foreach (var col in allCols)
        {
            if (wantedCols is not null && !wantedCols.Contains(col)) continue;
            if (indexed.TryGetValue(col, out var info))
                result.Add(new { column = col, has_index = true,  index = info.index, unique = info.unique });
            else
                result.Add(new { column = col, has_index = false, index = (string?)null, unique = false });
        }
        return result;
    }

    [McpServerTool, Description("Execute a DDL statement (CREATE/ALTER/DROP) and commit.")]
    public string ExecuteDdl(string database, string sql)
    {
        using var conn = Open(database);
        using var tx  = conn.BeginTransaction();
        using var cmd = new FbCommand(sql, conn, tx);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return "DDL executed and committed.";
    }

    [McpServerTool, Description("Execute INSERT, UPDATE, or DELETE and commit. Returns rows affected.")]
    public string ExecuteDml(string database, string sql)
    {
        using var conn = Open(database);
        using var tx  = conn.BeginTransaction();
        using var cmd = new FbCommand(sql, conn, tx);
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        return $"Done. Rows affected: {n}";
    }

    [McpServerTool, Description("Execute multiple semicolon-separated statements as a script.")]
    public string ExecuteScript(string database, string sqlScript)
    {
        var stmts = sqlScript.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = new List<string>();
        using var conn = Open(database);
        foreach (var stmt in stmts)
        {
            try
            {
                using var tx  = conn.BeginTransaction();
                using var cmd = new FbCommand(stmt, conn, tx);
                cmd.ExecuteNonQuery();
                tx.Commit();
                results.Add($"OK: {stmt[..Math.Min(60, stmt.Length)]}...");
            }
            catch (Exception ex)
            {
                results.Add($"ERROR: {ex.Message} on: {stmt[..Math.Min(60, stmt.Length)]}...");
            }
        }
        return string.Join("\n", results);
    }
}
