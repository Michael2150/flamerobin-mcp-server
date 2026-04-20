# flamerobin-mcp-server

A Model Context Protocol (MCP) server for Firebird databases. Reads connection details from [FlameRobin's](http://www.flamerobin.org/) `fr_databases.conf` so no extra configuration is needed.

## Building

```
dotnet build FirebirdMcp.csproj
```

## Publishing a single self-contained executable

```
dotnet publish FirebirdMcp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-single
```

The output is `publish-single\FirebirdMcp.exe`. The `publish-single\` folder is gitignored.

## Claude Desktop configuration

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "firebird": {
      "command": "C:\\path\\to\\publish-single\\FirebirdMcp.exe"
    }
  }
}
```

## Tools

| Tool | Description |
|------|-------------|
| `ListDatabases` | List all servers/databases registered in FlameRobin |
| `ListObjects` | List tables, views, or both — with optional regex filter |
| `ListProcedures` | List stored procedures — with optional regex filter |
| `ListTriggers` | List triggers — with optional regex filter |
| `ListGenerators` | List generators/sequences with current values |
| `ListRoles` | List roles — with optional regex filter |
| `DescribeTable` | Column definitions — supports `brief` mode and column filter |
| `GetForeignKeys` | FK relationships for a table (`in`, `out`, or `all` directions) |
| `GetTableConstraints` | PK, FK, UNIQUE, CHECK constraints for a table |
| `GetProcedureSource` | PSQL source of a stored procedure |
| `GetTriggerSource` | PSQL source of a trigger |
| `GetViewSource` | SQL source of a view |
| `GetExecutionPlan` | Execution plan for a SELECT — shows index usage |
| `AnalyzeMissingIndexes` | Which columns lack indexes; optionally scoped to filter columns |
| `GetDatabaseInfo` | ODS version, page size, dialect, etc. |
| `ListActiveConnections` | Active connections to the database |
| `RunQuery` | Execute a SELECT — supports `maxRows` and `columns` projection |
| `ExecuteDdl` | Execute CREATE/ALTER/DROP and commit |
| `ExecuteDml` | Execute INSERT/UPDATE/DELETE and commit |
| `ExecuteScript` | Execute multiple semicolon-separated statements |
