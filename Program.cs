using System.Runtime.InteropServices;
using System.Xml.Linq;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

static string GetFrConfPath()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "flamerobin", "fr_databases.conf");
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "flamerobin", "fr_databases.conf");
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".flamerobin", "fr_databases.conf");
}

var databases = new Dictionary<string, FbConnectionStringBuilder>(StringComparer.OrdinalIgnoreCase);
var doc = XDocument.Load(GetFrConfPath());
foreach (var srv in doc.Root!.Elements("server"))
{
    var srvName = srv.Element("name")?.Value?.Trim() ?? "";
    var host    = srv.Element("host")?.Value?.Trim() ?? "localhost";
    var port    = int.Parse(srv.Element("port")?.Value ?? "3050");
    foreach (var db in srv.Elements("database"))
    {
        var dbName = db.Element("name")?.Value?.Trim() ?? "";
        var key    = $"{srvName} / {dbName}";
        databases[key] = new FbConnectionStringBuilder
        {
            DataSource = host,
            Port       = port,
            Database   = db.Element("path")?.Value?.Trim() ?? "",
            UserID     = db.Element("username")?.Value?.Trim() ?? "SYSDBA",
            Password   = db.Element("password")?.Value?.Trim() ?? "",
            Charset    = db.Element("charset")?.Value?.Trim() ?? "UTF8",
            Role       = db.Element("role")?.Value?.Trim() ?? "",
        };
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddSingleton(databases)
    .AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name    = "Firebird MCP",
            Version = "1.0.0"
        };
        options.ServerInstructions =
            "This server exposes Firebird databases registered in FlameRobin. " +
            "Workflow: (1) call list_databases to get database keys; " +
            "(2) call get_schema_summary to understand the database layout before writing any SQL; " +
            "(3) call inspect_table for full structural detail on a specific table (columns, PK, indexes, FK) — " +
            "prefer this over calling describe_table + get_table_constraints + get_foreign_keys separately; " +
            "(4) finding tables or columns by name: " +
            "use search_schema(pattern, scope='tables') to find tables whose names match a regex, " +
            "search_schema(pattern, scope='columns') to find which tables contain a column matching a regex, " +
            "or get_schema_summary(filter) to get matching tables with their full column/PK/FK detail — " +
            "prefer these over listing everything and scanning manually; " +
            "(5) call get_execution_plan before run_query on large tables to verify an index will be used. " +
            "Firebird SQL notes: row limiting uses 'SELECT FIRST n' or 'ROWS n', NOT 'LIMIT n'. " +
            "String concatenation uses '||'. All identifiers are automatically uppercased by this server. " +
            "DDL is irreversible — always inspect the object first before ALTER or DROP.";
    })
    .WithStdioServerTransport()
    .WithTools<FirebirdTools>();

await builder.Build().RunAsync();
