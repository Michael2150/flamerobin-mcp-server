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
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<FirebirdTools>();

await builder.Build().RunAsync();
