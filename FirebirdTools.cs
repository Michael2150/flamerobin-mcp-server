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

    [McpServerTool, Description(
        "List all Firebird servers and databases registered in FlameRobin. " +
        "ALWAYS call this first to discover available databases. " +
        "Returns [{key, host, port, path}] where 'key' is the identifier required by every other tool's 'database' parameter.")]
    public List<object> ListDatabases(
        [Description("Optional case-insensitive .NET regex to filter results by key, host, or path. Omit to return all databases.")]
        string? filter = null)
    {
        var all = dbs.Select(kv => (object)new {
            key  = kv.Key,
            host = kv.Value.DataSource,
            port = kv.Value.Port,
            path = kv.Value.Database
        }).ToList();
        if (filter is null) return all;
        return all.Where(x => {
            dynamic d = x;
            return Regex.IsMatch((string)d.key,  filter, RegexOptions.IgnoreCase)
                || Regex.IsMatch((string)d.host, filter, RegexOptions.IgnoreCase)
                || Regex.IsMatch((string)d.path, filter, RegexOptions.IgnoreCase);
        }).ToList();
    }

    [McpServerTool, Description(
        "List user tables, views, or both in a Firebird database. " +
        "Returns a list of object names. Pass names to describe_table, get_table_constraints, get_foreign_keys, or get_view_source.")]
    public List<string> ListObjects(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Which objects to include: 'tables' (non-view relations only), 'views', or 'all' (default).")]
        string type = "all",
        [Description("Optional case-insensitive .NET regex to filter by object name.")]
        string? filter = null)
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

    [McpServerTool, Description(
        "List all user-defined stored procedures in the database. " +
        "Returns procedure names. Pass a name to get_procedure_source to read the PSQL body.")]
    public List<string> ListProcedures(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by procedure name.")]
        string? filter = null)
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
        "List all user-defined triggers, formatted as 'TRIGGER_NAME (on TABLE_NAME)'. " +
        "To retrieve a trigger's body, pass just the trigger name (without the ' (on TABLE)' suffix) to get_trigger_source.")]
    public List<string> ListTriggers(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex applied to the full 'TRIGGER_NAME (on TABLE)' string. " +
                     "Example: pass 'on INVOICES' to see only triggers on the INVOICES table.")]
        string? filter = null)
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

    [McpServerTool, Description(
        "List all user-defined generators (sequences / auto-increment counters) with their current values. " +
        "Returns [{name, value}].")]
    public List<object> ListGenerators(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by generator name.")]
        string? filter = null)
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

    [McpServerTool, Description(
        "List all roles defined in the database. Roles group privileges and are granted to users. " +
        "Returns a list of role names.")]
    public List<string> ListRoles(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by role name.")]
        string? filter = null)
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
        "Brief mode returns [{name, type, nullable}]. Full mode additionally returns length, precision, scale, default_src, description. " +
        "Use brief=true when you only need column names and types to reduce output size.")]
    public List<object> DescribeTable(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Exact table or view name from list_objects. Automatically uppercased.")]
        string table,
        [Description("If true, returns only {name, type, nullable} per column. Default false returns full detail.")]
        bool brief = false,
        [Description("Optional case-insensitive .NET regex to filter by column name.")]
        string? filter = null)
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

    [McpServerTool, Description(
        "Return the full PSQL source body of a stored procedure. " +
        "Returns the raw PSQL text, or an error message if the procedure is not found.")]
    public string GetProcedureSource(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Procedure name from list_procedures. Automatically uppercased.")]
        string procedure)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$PROCEDURE_SOURCE FROM RDB$PROCEDURES WHERE RDB$PROCEDURE_NAME=@p", conn);
        cmd.Parameters.AddWithValue("@p", procedure.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"Procedure '{procedure}' not found.";
    }

    [McpServerTool, Description(
        "Return the full PSQL source body of a trigger. " +
        "Returns the raw PSQL text, or an error message if the trigger is not found.")]
    public string GetTriggerSource(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Trigger name from list_triggers. Use just the name, NOT the ' (on TABLE)' suffix shown by list_triggers. Automatically uppercased.")]
        string trigger)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$TRIGGER_SOURCE FROM RDB$TRIGGERS WHERE RDB$TRIGGER_NAME=@t", conn);
        cmd.Parameters.AddWithValue("@t", trigger.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"Trigger '{trigger}' not found.";
    }

    [McpServerTool, Description(
        "Return the SELECT statement that defines a view. " +
        "Returns the view source SQL, or an error message if the view is not found.")]
    public string GetViewSource(
        [Description("Database key from list_databases.")]
        string database,
        [Description("View name from list_objects (type='views'). Automatically uppercased.")]
        string view)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$VIEW_SOURCE FROM RDB$RELATIONS WHERE RDB$RELATION_NAME=@v", conn);
        cmd.Parameters.AddWithValue("@v", view.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"View '{view}' not found.";
    }

    [McpServerTool, Description(
        "Return constraints on a table: PRIMARY KEY, FOREIGN KEY, UNIQUE, and CHECK. " +
        "Returns [{constraint, type, index}]. " +
        "Use typeFilter to request only one kind and reduce output size. " +
        "For full FK details (referenced columns, ON DELETE/UPDATE rules) use get_foreign_keys instead.")]
    public List<object> GetTableConstraints(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Table name from list_objects. Automatically uppercased.")]
        string table,
        [Description("Optional case-insensitive .NET regex to filter by constraint type. " +
                     "Common values: 'PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE', 'CHECK'. " +
                     "Omit to return all constraint types.")]
        string? typeFilter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$CONSTRAINT_NAME), TRIM(RDB$CONSTRAINT_TYPE), TRIM(RDB$INDEX_NAME) " +
            "FROM RDB$RELATION_CONSTRAINTS WHERE RDB$RELATION_NAME=@t ORDER BY 2,1", conn);
        cmd.Parameters.AddWithValue("@t", table.ToUpper());
        using var rdr = cmd.ExecuteReader();
        var r = new List<object>();
        while (rdr.Read())
        {
            var constraintType = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            if (typeFilter is not null && (constraintType is null || !Regex.IsMatch(constraintType, typeFilter, RegexOptions.IgnoreCase)))
                continue;
            r.Add(new {
                constraint = rdr.IsDBNull(0) ? null : rdr.GetString(0),
                type       = constraintType,
                index      = rdr.IsDBNull(2) ? null : rdr.GetString(2)
            });
        }
        return r;
    }

    [McpServerTool, Description(
        "Return physical metadata for a Firebird database file. " +
        "Returns {path, ods_major, ods_minor, page_size, pages, sql_dialect, sweep_interval}. " +
        "ods_major/ods_minor = On-Disk Structure version (indicates the Firebird engine version that created the file). " +
        "sql_dialect: 1 = legacy, 3 = standard/recommended. " +
        "pages = total allocated pages (multiply by page_size for approximate file size).")]
    public object GetDatabaseInfo(
        [Description("Database key from list_databases.")]
        string database)
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

    [McpServerTool, Description(
        "List all currently active non-system connections to the database (from MON$ATTACHMENTS). " +
        "Useful for checking who is connected before running DDL or maintenance. " +
        "Returns [{id, user, address, process, connected_at}].")]
    public List<object> ListActiveConnections(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by username. " +
                     "Example: 'SYSDBA' to show only admin connections, or 'APP_' to match a set of app users. " +
                     "Omit to return all active connections.")]
        string? userFilter = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT MON$ATTACHMENT_ID, TRIM(MON$USER), TRIM(MON$REMOTE_ADDRESS), " +
            "TRIM(MON$REMOTE_PROCESS), MON$TIMESTAMP " +
            "FROM MON$ATTACHMENTS WHERE MON$SYSTEM_FLAG=0 ORDER BY MON$TIMESTAMP", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<object>();
        while (rdr.Read())
        {
            var user = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            if (userFilter is not null && (user is null || !Regex.IsMatch(user, userFilter, RegexOptions.IgnoreCase)))
                continue;
            r.Add(new {
                id           = rdr.GetValue(0),
                user,
                address      = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                process      = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                connected_at = rdr.IsDBNull(4) ? (object?)null : rdr.GetValue(4)
            });
        }
        return r;
    }

    [McpServerTool, Description(
        "Execute a read-only SELECT query and return results as a list of row objects. " +
        "If you are unsure about column names, types, or nullability, call describe_table first — do not guess schema. " +
        "IMPORTANT Firebird SQL syntax differences from other databases: " +
        "use 'SELECT FIRST n ... FROM ...' or 'SELECT ... FROM ... ROWS n' to limit rows — " +
        "'LIMIT n' is NOT valid Firebird SQL. " +
        "String concatenation uses '||' not '+'. " +
        "Returns [{column: value, ...}] per row.")]
    public List<Dictionary<string, object?>> RunQuery(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A SELECT statement using Firebird SQL syntax. Do not include a trailing semicolon. " +
                     "Use 'SELECT FIRST n' or 'ROWS n' to limit result size, not 'LIMIT n'.")]
        string sql,
        [Description("Maximum number of rows to return. Defaults to 500. Increase only when you need more rows; large values may produce very large responses.")]
        int maxRows = 500,
        [Description("Comma-separated list of column names to include in results. Useful to reduce noise from wide tables. Omit to return all columns.")]
        string? columns = null)
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
        "Return foreign key relationships involving a table, in one or both directions. " +
        "Returns [{direction, from_table, from_column, to_table, to_column, on_update, on_delete}]. " +
        "direction='out' shows what other tables this table depends on; direction='in' shows what other tables reference this table. " +
        "Use relatedTable to focus on a single relationship and reduce output size.")]
    public List<object> GetForeignKeys(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Table name from list_objects. Automatically uppercased.")]
        string table,
        [Description("Which FK relationships to return: " +
                     "'out' = FK constraints declared on this table (references to other tables), " +
                     "'in' = FK constraints on other tables that point to this table, " +
                     "'all' (default) = both directions.")]
        string direction = "all",
        [Description("Optional case-insensitive .NET regex to filter by the other table name. " +
                     "For direction='out' this filters to_table; for direction='in' this filters from_table. " +
                     "Useful when a table has many FK relationships and you only care about one. Omit to return all.")]
        string? relatedTable = null)
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
            {
                var toTable = rdr.GetString(2);
                if (relatedTable is not null && !Regex.IsMatch(toTable, relatedTable, RegexOptions.IgnoreCase))
                    continue;
                result.Add(new {
                    direction   = "out",
                    from_table  = rdr.GetString(0),
                    from_column = rdr.GetString(1),
                    to_table    = toTable,
                    to_column   = rdr.GetString(3),
                    on_update   = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    on_delete   = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                });
            }
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
            {
                var fromTable = rdr.GetString(0);
                if (relatedTable is not null && !Regex.IsMatch(fromTable, relatedTable, RegexOptions.IgnoreCase))
                    continue;
                result.Add(new {
                    direction   = "in",
                    from_table  = fromTable,
                    from_column = rdr.GetString(1),
                    to_table    = rdr.GetString(2),
                    to_column   = rdr.GetString(3),
                    on_update   = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    on_delete   = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                });
            }
        }

        return result;
    }

    [McpServerTool, Description(
        "Return the query execution plan for a SELECT — shows which indexes Firebird will use. " +
        "The query is only prepared, never executed, so it is safe to use on large tables. " +
        "Returns a plan string such as 'PLAN (TABLE NATURAL)' or 'PLAN (TABLE INDEX (IDX_NAME))'. " +
        "Use this before run_query to detect accidental full-table scans.")]
    public string GetExecutionPlan(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A SELECT statement to analyse. Do not include a trailing semicolon. DML statements are not supported.")]
        string sql)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(sql, conn);
        cmd.Prepare();
        return cmd.GetCommandPlan() ?? "No plan returned.";
    }

    [McpServerTool, Description(
        "Report which columns on a table are covered by an active index (as the leading segment) and which are not. " +
        "Helps identify missing indexes on columns used in WHERE clauses or JOINs. " +
        "Returns [{column, has_index, index (name or null), unique}].")]
    public List<object> AnalyzeMissingIndexes(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Table name from list_objects. Automatically uppercased.")]
        string table,
        [Description("Comma-separated column names to check. Omit to check all columns on the table. " +
                     "Supply specific column names when you only care about columns used in WHERE clauses or JOINs.")]
        string? filterColumns = null)
    {
        var wantedCols = filterColumns?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpper()).ToHashSet()
            ?? null;

        using var conn = Open(database);

        using var colCmd = new FbCommand(
            "SELECT TRIM(RDB$FIELD_NAME) FROM RDB$RELATION_FIELDS " +
            "WHERE RDB$RELATION_NAME=@t ORDER BY RDB$FIELD_POSITION", conn);
        colCmd.Parameters.AddWithValue("@t", table.ToUpper());
        var allCols = new List<string>();
        using (var rdr = colCmd.ExecuteReader())
            while (rdr.Read()) allCols.Add(rdr.GetString(0));

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

    [McpServerTool, Description(
        "Execute a single DDL statement (CREATE TABLE, ALTER TABLE, DROP TABLE, CREATE INDEX, etc.) and auto-commit. " +
        "Before altering or dropping an existing object, call describe_table or list_objects to confirm it exists and understand its current structure. " +
        "WARNING: DDL is irreversible — dropped tables and columns cannot be recovered. " +
        "For multiple DDL statements use execute_script. " +
        "Returns 'DDL executed and committed.' on success, or throws on error.")]
    public string ExecuteDdl(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A single DDL statement. Do NOT include a trailing semicolon. " +
                     "Examples: 'CREATE TABLE FOO (ID INTEGER NOT NULL)', 'ALTER TABLE FOO ADD COLUMN BAR VARCHAR(100)', 'DROP TABLE FOO'.")]
        string sql)
    {
        using var conn = Open(database);
        using var tx  = conn.BeginTransaction();
        using var cmd = new FbCommand(sql, conn, tx);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return "DDL executed and committed.";
    }

    [McpServerTool, Description(
        "Execute a single INSERT, UPDATE, or DELETE statement in a transaction and commit. " +
        "If you are unsure about column names, types, or constraints, call describe_table and get_table_constraints first — do not guess schema. " +
        "Throws and rolls back on error. " +
        "For multiple statements use execute_script. " +
        "Returns 'Done. Rows affected: N'.")]
    public string ExecuteDml(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A single INSERT, UPDATE, or DELETE statement. Do NOT include a trailing semicolon. " +
                     "Use Firebird parameter syntax (@param) to avoid SQL injection when values are user-supplied.")]
        string sql)
    {
        using var conn = Open(database);
        using var tx  = conn.BeginTransaction();
        using var cmd = new FbCommand(sql, conn, tx);
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        return $"Done. Rows affected: {n}";
    }

    [McpServerTool, Description(
        "Execute multiple SQL statements separated by semicolons. " +
        "Each statement runs in its own transaction and is committed independently — " +
        "a failure on one statement does NOT roll back previously committed statements. " +
        "Returns one status line per statement: 'OK: <first 60 chars>' or 'ERROR: <message> on: <first 60 chars>'. " +
        "For a single statement, prefer execute_ddl or execute_dml instead.")]
    public string ExecuteScript(
        [Description("Database key from list_databases.")]
        string database,
        [Description("One or more DDL or DML statements separated by semicolons. " +
                     "Each statement is trimmed and executed individually. " +
                     "WARNING: statements already committed before a later failure cannot be rolled back.")]
        string sqlScript)
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
