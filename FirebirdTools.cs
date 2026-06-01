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

    static List<string> FilterNames(List<string> names, string? filter) =>
        filter is null ? names : names.Where(n => Regex.IsMatch(n, filter, RegexOptions.IgnoreCase)).ToList();

    static List<T> ApplyLimit<T>(List<T> list, int? limit) =>
        limit.HasValue ? list.Take(limit.Value).ToList() : list;

    // ── Discovery ────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "List all Firebird servers and databases registered in FlameRobin. " +
        "ALWAYS call this first — the 'key' in each result is required by every other tool's 'database' parameter. " +
        "Returns [{key, host, port, path}].")]
    public List<object> ListDatabases(
        [Description("Optional case-insensitive .NET regex to filter by key, host, or path. Omit to return all.")]
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
        "Return a compact overview of every user table in the database: column names with types, primary key, " +
        "and foreign key counts. Use this as the first step when exploring an unfamiliar database — " +
        "it replaces calling list_objects + describe_table on every table. " +
        "Returns [{table, columns: [\"NAME:TYPE\"], pk: [\"COL\"], fk_out, fk_in}].")]
    public List<object> GetSchemaSummary(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by table name.")]
        string? filter = null,
        [Description("Maximum number of tables to return. Omit for all tables. Use with filter to focus on a subsection of a large schema.")]
        int? limit = null)
    {
        using var conn = Open(database);

        // All columns for all user tables in one query
        var colMap = new Dictionary<string, List<string>>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(r.RDB$RELATION_NAME), TRIM(rf.RDB$FIELD_NAME), TRIM(t.RDB$TYPE_NAME)
            FROM RDB$RELATIONS r
            JOIN RDB$RELATION_FIELDS rf ON rf.RDB$RELATION_NAME = r.RDB$RELATION_NAME
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            JOIN RDB$TYPES t ON t.RDB$TYPE = f.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
            WHERE r.RDB$SYSTEM_FLAG = 0
            ORDER BY r.RDB$RELATION_NAME, rf.RDB$FIELD_POSITION", conn))
        using (var rdr = cmd.ExecuteReader())
            while (rdr.Read())
            {
                var tbl = rdr.GetString(0);
                if (!colMap.TryGetValue(tbl, out var cols)) colMap[tbl] = cols = new List<string>();
                cols.Add($"{rdr.GetString(1)}:{(rdr.IsDBNull(2) ? "?" : rdr.GetString(2))}");
            }

        // Primary key columns per table
        var pkMap = new Dictionary<string, List<string>>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(rc.RDB$RELATION_NAME), TRIM(seg.RDB$FIELD_NAME)
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$INDEX_SEGMENTS seg ON seg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            WHERE rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY rc.RDB$RELATION_NAME, seg.RDB$FIELD_POSITION", conn))
        using (var rdr = cmd.ExecuteReader())
            while (rdr.Read())
            {
                var tbl = rdr.GetString(0);
                if (!pkMap.TryGetValue(tbl, out var pk)) pkMap[tbl] = pk = new List<string>();
                pk.Add(rdr.GetString(1));
            }

        // FK counts per table (out = declared here, in = others pointing here)
        var fkOut = new Dictionary<string, int>();
        var fkIn  = new Dictionary<string, int>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(rc.RDB$RELATION_NAME), TRIM(rc2.RDB$RELATION_NAME)
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$REF_CONSTRAINTS refc ON refc.RDB$CONSTRAINT_NAME = rc.RDB$CONSTRAINT_NAME
            JOIN RDB$RELATION_CONSTRAINTS rc2 ON rc2.RDB$CONSTRAINT_NAME = refc.RDB$CONST_NAME_UQ
            WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY'", conn))
        using (var rdr = cmd.ExecuteReader())
            while (rdr.Read())
            {
                var from = rdr.GetString(0);
                var to   = rdr.GetString(1);
                fkOut[from] = fkOut.GetValueOrDefault(from) + 1;
                fkIn[to]    = fkIn.GetValueOrDefault(to)    + 1;
            }

        var tables = colMap.Keys.ToList();
        tables = FilterNames(tables, filter);
        tables.Sort();
        tables = ApplyLimit(tables, limit);

        return tables.Select(t => (object)new {
            table   = t,
            columns = colMap.GetValueOrDefault(t) ?? [],
            pk      = pkMap.GetValueOrDefault(t)  ?? [],
            fk_out  = fkOut.GetValueOrDefault(t),
            fk_in   = fkIn.GetValueOrDefault(t)
        }).ToList();
    }

    [McpServerTool, Description(
        "Search for tables or columns whose names match a pattern. " +
        "Use this instead of listing all objects when you know part of a name — e.g. find every table " +
        "containing 'ORDER', or every column named 'CUSTOMER_ID'. " +
        "Returns {tables: [\"NAME\"], columns: [{table, column, type}]}.")]
    public object SearchSchema(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Case-insensitive .NET regex pattern to search for. Applied to object/column names.")]
        string pattern,
        [Description("What to search: 'tables' (table/view names only), 'columns' (column names only), 'all' (default, both).")]
        string scope = "all",
        [Description("Maximum number of results per category (tables and columns each). Defaults to 50.")]
        int limit = 50)
    {
        using var conn = Open(database);
        var tables  = new List<string>();
        var columns = new List<object>();

        if (scope is "tables" or "all")
        {
            using var cmd = new FbCommand(
                "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE RDB$SYSTEM_FLAG=0 ORDER BY 1", conn);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read() && tables.Count < limit)
            {
                var name = rdr.GetString(0);
                if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
                    tables.Add(name);
            }
        }

        if (scope is "columns" or "all")
        {
            using var cmd = new FbCommand(@"
                SELECT TRIM(rf.RDB$RELATION_NAME), TRIM(rf.RDB$FIELD_NAME), TRIM(t.RDB$TYPE_NAME)
                FROM RDB$RELATION_FIELDS rf
                JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
                JOIN RDB$TYPES t ON t.RDB$TYPE = f.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
                JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = rf.RDB$RELATION_NAME
                WHERE r.RDB$SYSTEM_FLAG = 0
                ORDER BY rf.RDB$RELATION_NAME, rf.RDB$FIELD_POSITION", conn);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read() && columns.Count < limit)
            {
                var col = rdr.GetString(1);
                if (Regex.IsMatch(col, pattern, RegexOptions.IgnoreCase))
                    columns.Add(new {
                        table  = rdr.GetString(0),
                        column = col,
                        type   = rdr.IsDBNull(2) ? null : rdr.GetString(2)
                    });
            }
        }

        return new { tables, columns };
    }

    // ── Table inspection ─────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Return full structural details for a single table or view in one call: " +
        "column definitions, primary key, all indexes, and foreign keys in both directions. " +
        "Prefer this over calling describe_table + get_table_constraints + get_foreign_keys separately. " +
        "Returns {table, columns:[{name,type,nullable,default_src}], primary_key:[col], " +
        "indexes:[{name,columns,unique}], foreign_keys_out:[{column,references,on_update,on_delete}], " +
        "foreign_keys_in:[{from_table,from_column,on_column,on_update,on_delete}]}.")]
    public object InspectTable(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Exact table or view name from list_objects or get_schema_summary. Automatically uppercased.")]
        string table)
    {
        using var conn = Open(database);
        var t = table.ToUpper();

        // Columns
        var columns = new List<object>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(rf.RDB$FIELD_NAME), TRIM(tp.RDB$TYPE_NAME),
                   COALESCE(rf.RDB$NULL_FLAG,0), rf.RDB$DEFAULT_SOURCE
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            JOIN RDB$TYPES tp ON tp.RDB$TYPE = f.RDB$FIELD_TYPE AND tp.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
            WHERE rf.RDB$RELATION_NAME = @t ORDER BY rf.RDB$FIELD_POSITION", conn))
        {
            cmd.Parameters.AddWithValue("@t", t);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                columns.Add(new {
                    name        = rdr.GetString(0),
                    type        = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                    nullable    = rdr.GetInt16(2) == 0,
                    default_src = rdr.IsDBNull(3) ? null : rdr.GetString(3)?.Trim()
                });
        }

        // Primary key
        var pk = new List<string>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(seg.RDB$FIELD_NAME)
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$INDEX_SEGMENTS seg ON seg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            WHERE rc.RDB$RELATION_NAME = @t AND rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY seg.RDB$FIELD_POSITION", conn))
        {
            cmd.Parameters.AddWithValue("@t", t);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) pk.Add(rdr.GetString(0));
        }

        // Indexes (all segments, active only)
        var indexMap = new Dictionary<string, (bool unique, List<string> cols)>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(idx.RDB$INDEX_NAME), TRIM(seg.RDB$FIELD_NAME), COALESCE(idx.RDB$UNIQUE_FLAG,0)
            FROM RDB$INDICES idx
            JOIN RDB$INDEX_SEGMENTS seg ON seg.RDB$INDEX_NAME = idx.RDB$INDEX_NAME
            WHERE idx.RDB$RELATION_NAME = @t AND COALESCE(idx.RDB$INDEX_INACTIVE,0) = 0
            ORDER BY idx.RDB$INDEX_NAME, seg.RDB$FIELD_POSITION", conn))
        {
            cmd.Parameters.AddWithValue("@t", t);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var iname  = rdr.GetString(0);
                var unique = rdr.GetInt16(2) != 0;
                if (!indexMap.TryGetValue(iname, out var entry))
                    indexMap[iname] = entry = (unique, new List<string>());
                entry.cols.Add(rdr.GetString(1));
            }
        }
        var indexes = indexMap.Select(kv => (object)new {
            name    = kv.Key,
            columns = kv.Value.cols,
            unique  = kv.Value.unique
        }).ToList();

        // FK out (this table → others)
        var fkOut = new List<object>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(iseg.RDB$FIELD_NAME), TRIM(rc2.RDB$RELATION_NAME),
                   TRIM(iseg2.RDB$FIELD_NAME), TRIM(refc.RDB$UPDATE_RULE), TRIM(refc.RDB$DELETE_RULE)
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$REF_CONSTRAINTS refc ON refc.RDB$CONSTRAINT_NAME = rc.RDB$CONSTRAINT_NAME
            JOIN RDB$RELATION_CONSTRAINTS rc2 ON rc2.RDB$CONSTRAINT_NAME = refc.RDB$CONST_NAME_UQ
            JOIN RDB$INDEX_SEGMENTS iseg ON iseg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            JOIN RDB$INDEX_SEGMENTS iseg2 ON iseg2.RDB$INDEX_NAME = rc2.RDB$INDEX_NAME
                                          AND iseg2.RDB$FIELD_POSITION = iseg.RDB$FIELD_POSITION
            WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' AND rc.RDB$RELATION_NAME = @t
            ORDER BY iseg.RDB$FIELD_POSITION", conn))
        {
            cmd.Parameters.AddWithValue("@t", t);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                fkOut.Add(new {
                    column     = rdr.GetString(0),
                    references = $"{rdr.GetString(1)}.{rdr.GetString(2)}",
                    on_update  = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    on_delete  = rdr.IsDBNull(4) ? null : rdr.GetString(4)
                });
        }

        // FK in (others → this table)
        var fkIn = new List<object>();
        using (var cmd = new FbCommand(@"
            SELECT TRIM(rc.RDB$RELATION_NAME), TRIM(iseg.RDB$FIELD_NAME),
                   TRIM(iseg2.RDB$FIELD_NAME), TRIM(refc.RDB$UPDATE_RULE), TRIM(refc.RDB$DELETE_RULE)
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$REF_CONSTRAINTS refc ON refc.RDB$CONSTRAINT_NAME = rc.RDB$CONSTRAINT_NAME
            JOIN RDB$RELATION_CONSTRAINTS rc2 ON rc2.RDB$CONSTRAINT_NAME = refc.RDB$CONST_NAME_UQ
            JOIN RDB$INDEX_SEGMENTS iseg ON iseg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            JOIN RDB$INDEX_SEGMENTS iseg2 ON iseg2.RDB$INDEX_NAME = rc2.RDB$INDEX_NAME
                                          AND iseg2.RDB$FIELD_POSITION = iseg.RDB$FIELD_POSITION
            WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' AND rc2.RDB$RELATION_NAME = @t
            ORDER BY rc.RDB$RELATION_NAME, iseg.RDB$FIELD_POSITION", conn))
        {
            cmd.Parameters.AddWithValue("@t", t);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                fkIn.Add(new {
                    from_table  = rdr.GetString(0),
                    from_column = rdr.GetString(1),
                    on_column   = rdr.GetString(2),
                    on_update   = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    on_delete   = rdr.IsDBNull(4) ? null : rdr.GetString(4)
                });
        }

        return new { table = t, columns, primary_key = pk, indexes, foreign_keys_out = fkOut, foreign_keys_in = fkIn };
    }

    [McpServerTool, Description(
        "List user tables, views, or both in a Firebird database. " +
        "Returns object names. For a richer starting point use get_schema_summary instead. " +
        "Pass individual names to inspect_table, get_table_constraints, get_foreign_keys, or get_view_source.")]
    public List<string> ListObjects(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Which objects to include: 'tables' (non-view relations only), 'views', or 'all' (default).")]
        string type = "all",
        [Description("Optional case-insensitive .NET regex to filter by object name.")]
        string? filter = null,
        [Description("Maximum number of results to return. Omit for all.")]
        int? limit = null)
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
        return ApplyLimit(FilterNames(r, filter), limit);
    }

    [McpServerTool, Description(
        "Return column definitions for a table or view. " +
        "Prefer inspect_table when you also need constraints or FK relationships. " +
        "Brief mode returns [{name, type, nullable}]. Full mode adds length, precision, scale, default_src, description. " +
        "If unsure about column names or types, call this before writing any query or DML — do not guess schema.")]
    public List<object> DescribeTable(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Exact table or view name. Automatically uppercased.")]
        string table,
        [Description("If true, returns only {name, type, nullable}. Default false returns full column detail.")]
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
                result.Add(new { name, type = rdr.IsDBNull(1) ? null : rdr.GetString(1), nullable = rdr.GetInt16(5) == 0 });
            else
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
        return result;
    }

    [McpServerTool, Description(
        "Return constraints on a table: PRIMARY KEY, FOREIGN KEY, UNIQUE, and CHECK. " +
        "Returns [{constraint, type, index}]. Prefer inspect_table to get constraints alongside columns and FKs. " +
        "For full FK details (referenced columns, ON DELETE/UPDATE rules) use get_foreign_keys instead.")]
    public List<object> GetTableConstraints(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Table name. Automatically uppercased.")]
        string table,
        [Description("Optional case-insensitive .NET regex to filter by constraint type. " +
                     "Values: 'PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE', 'CHECK'. Omit to return all.")]
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
        "Return foreign key relationships involving a table, in one or both directions. " +
        "Returns [{direction, from_table, from_column, to_table, to_column, on_update, on_delete}]. " +
        "Prefer inspect_table to get FKs alongside column and index information in one call. " +
        "direction='out': what other tables this table depends on. direction='in': what other tables reference this table.")]
    public List<object> GetForeignKeys(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Table name. Automatically uppercased.")]
        string table,
        [Description("'out' = FKs declared on this table, 'in' = FKs on other tables pointing here, 'all' (default) = both.")]
        string direction = "all",
        [Description("Optional case-insensitive .NET regex to filter by the other table name. " +
                     "Filters to_table for direction='out', from_table for direction='in'.")]
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
                WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' AND rc.RDB$RELATION_NAME = @t
                ORDER BY iseg.RDB$FIELD_POSITION", conn);
            cmd.Parameters.AddWithValue("@t", table.ToUpper());
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var toTable = rdr.GetString(2);
                if (relatedTable is not null && !Regex.IsMatch(toTable, relatedTable, RegexOptions.IgnoreCase)) continue;
                result.Add(new {
                    direction   = "out",
                    from_table  = rdr.GetString(0), from_column = rdr.GetString(1),
                    to_table    = toTable,           to_column   = rdr.GetString(3),
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
                WHERE rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY' AND rc2.RDB$RELATION_NAME = @t
                ORDER BY rc.RDB$RELATION_NAME, iseg.RDB$FIELD_POSITION", conn);
            cmd.Parameters.AddWithValue("@t", table.ToUpper());
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var fromTable = rdr.GetString(0);
                if (relatedTable is not null && !Regex.IsMatch(fromTable, relatedTable, RegexOptions.IgnoreCase)) continue;
                result.Add(new {
                    direction   = "in",
                    from_table  = fromTable,         from_column = rdr.GetString(1),
                    to_table    = rdr.GetString(2),  to_column   = rdr.GetString(3),
                    on_update   = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    on_delete   = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                });
            }
        }

        return result;
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

    // ── Procedures & triggers ─────────────────────────────────────────────────

    [McpServerTool, Description(
        "List all user-defined stored procedures. " +
        "Returns procedure names. Call describe_procedure for parameter signatures, or get_procedure_source for the PSQL body.")]
    public List<string> ListProcedures(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by procedure name.")]
        string? filter = null,
        [Description("Maximum number of results to return. Omit for all.")]
        int? limit = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$PROCEDURE_NAME) FROM RDB$PROCEDURES WHERE RDB$SYSTEM_FLAG=0 ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return ApplyLimit(FilterNames(r, filter), limit);
    }

    [McpServerTool, Description(
        "Return the input and output parameter signatures for a stored procedure without reading the full PSQL body. " +
        "Use this to understand how to call a procedure. Call get_procedure_source only when you need the implementation. " +
        "Returns {name, input_params:[{name,type}], output_params:[{name,type}]}.")]
    public object DescribeProcedure(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Procedure name from list_procedures. Automatically uppercased.")]
        string procedure)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(@"
            SELECT TRIM(pp.RDB$PARAMETER_NAME), TRIM(t.RDB$TYPE_NAME), pp.RDB$PARAMETER_TYPE
            FROM RDB$PROCEDURE_PARAMETERS pp
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = pp.RDB$FIELD_SOURCE
            JOIN RDB$TYPES t ON t.RDB$TYPE = f.RDB$FIELD_TYPE AND t.RDB$FIELD_NAME = 'RDB$FIELD_TYPE'
            WHERE pp.RDB$PROCEDURE_NAME = @p
            ORDER BY pp.RDB$PARAMETER_TYPE, pp.RDB$PARAMETER_NUMBER", conn);
        cmd.Parameters.AddWithValue("@p", procedure.ToUpper());
        var input  = new List<object>();
        var output = new List<object>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var param = new { name = rdr.GetString(0), type = rdr.IsDBNull(1) ? null : rdr.GetString(1) };
            if (rdr.GetInt16(2) == 0) input.Add(param); else output.Add(param);
        }
        return new { name = procedure.ToUpper(), input_params = input, output_params = output };
    }

    [McpServerTool, Description(
        "Return the full PSQL source body of a stored procedure. " +
        "Call describe_procedure first if you only need to know parameters — not the implementation. " +
        "Returns the raw PSQL text, or an error message if not found.")]
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
        "List all user-defined triggers, formatted as 'TRIGGER_NAME (on TABLE_NAME)'. " +
        "To retrieve a trigger body, pass just the name (without the ' (on TABLE)' suffix) to get_trigger_source.")]
    public List<string> ListTriggers(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex on the full 'TRIGGER_NAME (on TABLE)' string. " +
                     "Example: 'on INVOICES' to see triggers on a specific table.")]
        string? filter = null,
        [Description("Maximum number of results to return. Omit for all.")]
        int? limit = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$TRIGGER_NAME)||' (on '||TRIM(RDB$RELATION_NAME)||')' " +
            "FROM RDB$TRIGGERS WHERE RDB$SYSTEM_FLAG=0 ORDER BY 2,1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return ApplyLimit(FilterNames(r, filter), limit);
    }

    [McpServerTool, Description(
        "Return the full PSQL source body of a trigger. " +
        "Returns the raw PSQL text, or an error message if not found.")]
    public string GetTriggerSource(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Trigger name from list_triggers. Use just the name — NOT the ' (on TABLE)' suffix. Automatically uppercased.")]
        string trigger)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT RDB$TRIGGER_SOURCE FROM RDB$TRIGGERS WHERE RDB$TRIGGER_NAME=@t", conn);
        cmd.Parameters.AddWithValue("@t", trigger.ToUpper());
        return cmd.ExecuteScalar()?.ToString() ?? $"Trigger '{trigger}' not found.";
    }

    // ── Generators & roles ────────────────────────────────────────────────────

    [McpServerTool, Description(
        "List user-defined generators (sequences / auto-increment counters) with their current values. " +
        "Returns [{name, value}].")]
    public List<object> ListGenerators(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by name.")]
        string? filter = null,
        [Description("Maximum number of results to return. Omit for all.")]
        int? limit = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(
            "SELECT TRIM(RDB$GENERATOR_NAME), GEN_ID(RDB$GENERATOR_NAME,0) " +
            "FROM RDB$GENERATORS WHERE RDB$SYSTEM_FLAG=0 ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<object>();
        while (rdr.Read()) r.Add(new { name = rdr.GetString(0), value = rdr.GetValue(1) });
        var filtered = filter is null ? r
            : r.Where(x => Regex.IsMatch(((dynamic)x).name, filter, RegexOptions.IgnoreCase)).ToList();
        return ApplyLimit(filtered, limit);
    }

    [McpServerTool, Description(
        "List all roles defined in the database. Roles group privileges and are granted to users.")]
    public List<string> ListRoles(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by role name.")]
        string? filter = null,
        [Description("Maximum number of results to return. Omit for all.")]
        int? limit = null)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand("SELECT TRIM(RDB$ROLE_NAME) FROM RDB$ROLES ORDER BY 1", conn);
        using var rdr = cmd.ExecuteReader();
        var r = new List<string>();
        while (rdr.Read()) r.Add(rdr.GetString(0));
        return ApplyLimit(FilterNames(r, filter), limit);
    }

    // ── Monitoring ────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Return physical metadata for a Firebird database file. " +
        "Returns {path, ods_major, ods_minor, page_size, pages, sql_dialect, sweep_interval}. " +
        "ods_major/ods_minor indicate the Firebird engine version. sql_dialect: 1=legacy, 3=standard. " +
        "pages × page_size = approximate file size in bytes.")]
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
            ods_major      = rdr.GetValue(1),  ods_minor      = rdr.GetValue(2),
            page_size      = rdr.GetValue(3),  pages          = rdr.GetValue(4),
            sql_dialect    = rdr.GetValue(5),  sweep_interval = rdr.GetValue(6)
        };
    }

    [McpServerTool, Description(
        "List active non-system connections to the database. " +
        "Useful before running DDL or maintenance to check who is connected. " +
        "Returns [{id, user, address, process, connected_at}].")]
    public List<object> ListActiveConnections(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Optional case-insensitive .NET regex to filter by username. " +
                     "Example: 'SYSDBA' for admin connections only.")]
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
                id           = rdr.GetValue(0), user,
                address      = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                process      = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                connected_at = rdr.IsDBNull(4) ? (object?)null : rdr.GetValue(4)
            });
        }
        return r;
    }

    // ── Querying ──────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Execute a read-only SELECT query and return results as row objects. " +
        "If unsure about column names or types, call describe_table or inspect_table first — do not guess schema. " +
        "IMPORTANT Firebird SQL differences: use 'SELECT FIRST n' or 'ROWS n' to limit rows — 'LIMIT n' is invalid. " +
        "String concatenation uses '||' not '+'. " +
        "Returns [{column: value, ...}] per row.")]
    public List<Dictionary<string, object?>> RunQuery(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A SELECT statement in Firebird SQL. No trailing semicolon. " +
                     "Use 'SELECT FIRST n' or 'ROWS n' to cap rows at the SQL level.")]
        string sql,
        [Description("Server-side row cap. Defaults to 100. Raise only when you genuinely need more rows.")]
        int maxRows = 100,
        [Description("Comma-separated column names to include. Omit to return all columns. Use to reduce output on wide tables.")]
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
        "Return the query execution plan for a SELECT — shows which indexes Firebird will use. " +
        "The query is prepared but never executed, so it is safe on large tables. " +
        "Use before run_query to detect accidental full-table scans. " +
        "Returns a plan string such as 'PLAN (TABLE NATURAL)' or 'PLAN (TABLE INDEX (IDX_NAME))'.")]
    public string GetExecutionPlan(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A SELECT statement to analyse. No trailing semicolon. DML not supported.")]
        string sql)
    {
        using var conn = Open(database);
        using var cmd = new FbCommand(sql, conn);
        cmd.Prepare();
        return cmd.GetCommandPlan() ?? "No plan returned.";
    }

    [McpServerTool, Description(
        "Report which columns on a table are covered by an active index (as the leading segment). " +
        "Helps identify missing indexes on columns used in WHERE clauses or JOINs. " +
        "Returns [{column, has_index, index, unique}].")]
    public List<object> AnalyzeMissingIndexes(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Table name. Automatically uppercased.")]
        string table,
        [Description("Comma-separated column names to check. Omit to check all columns.")]
        string? filterColumns = null)
    {
        var wantedCols = filterColumns?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToUpper()).ToHashSet();

        using var conn = Open(database);

        using var colCmd = new FbCommand(
            "SELECT TRIM(RDB$FIELD_NAME) FROM RDB$RELATION_FIELDS " +
            "WHERE RDB$RELATION_NAME=@t ORDER BY RDB$FIELD_POSITION", conn);
        colCmd.Parameters.AddWithValue("@t", table.ToUpper());
        var allCols = new List<string>();
        using (var rdr = colCmd.ExecuteReader())
            while (rdr.Read()) allCols.Add(rdr.GetString(0));

        using var idxCmd = new FbCommand(@"
            SELECT TRIM(seg.RDB$FIELD_NAME), TRIM(idx.RDB$INDEX_NAME), COALESCE(idx.RDB$UNIQUE_FLAG,0)
            FROM RDB$INDEX_SEGMENTS seg
            JOIN RDB$INDICES idx ON idx.RDB$INDEX_NAME = seg.RDB$INDEX_NAME
            WHERE idx.RDB$RELATION_NAME = @t
              AND seg.RDB$FIELD_POSITION = 0
              AND COALESCE(idx.RDB$INDEX_INACTIVE,0) = 0", conn);
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
                result.Add(new { column = col, has_index = true,  index = (string?)info.index, unique = info.unique });
            else
                result.Add(new { column = col, has_index = false, index = (string?)null, unique = false });
        }
        return result;
    }

    // ── DDL / DML ─────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Execute a single DDL statement (CREATE/ALTER/DROP TABLE, CREATE INDEX, etc.) and auto-commit. " +
        "Before altering or dropping an existing object, call inspect_table or list_objects first to confirm structure. " +
        "WARNING: DDL is irreversible — dropped tables and columns cannot be recovered. " +
        "For multiple statements use execute_script. Returns 'DDL executed and committed.' on success.")]
    public string ExecuteDdl(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A single DDL statement. No trailing semicolon.")]
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
        "Execute a single INSERT, UPDATE, or DELETE statement and commit. " +
        "If unsure about column names or constraints, call inspect_table first — do not guess schema. " +
        "Throws and rolls back on error. For multiple statements use execute_script. " +
        "Returns 'Done. Rows affected: N'.")]
    public string ExecuteDml(
        [Description("Database key from list_databases.")]
        string database,
        [Description("A single INSERT, UPDATE, or DELETE statement. No trailing semicolon.")]
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
        "Execute multiple SQL statements separated by semicolons. Each runs in its own transaction. " +
        "A failure on one statement does NOT roll back previously committed statements — there is no global rollback. " +
        "Returns one status line per statement: 'OK: ...' or 'ERROR: <message> on: ...'. " +
        "For a single statement prefer execute_ddl or execute_dml.")]
    public string ExecuteScript(
        [Description("Database key from list_databases.")]
        string database,
        [Description("Semicolon-separated DDL or DML statements. Each is trimmed and committed independently. " +
                     "WARNING: already-committed statements cannot be rolled back if a later one fails.")]
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
