using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Pipedl.Infrastructure;

public class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public IDbConnection CreateConnection()
    {
        var normalizedConnectionString = NormalizeConnectionString(_connectionString);
        var ensuredConnectionString = EnsureDatabaseExists(normalizedConnectionString);
        return new SqliteConnection(ensuredConnectionString);
    }

    private static string EnsureDatabaseExists(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var path = builder.DataSource;

        if (string.IsNullOrEmpty(path) || path.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        // Avoid treating SQLite URI data sources as plain file paths.
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, AppContext.BaseDirectory);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (!File.Exists(fullPath))
            using (File.Create(fullPath)) { }

        builder.DataSource = fullPath;
        return builder.ToString();
    }

    private static string NormalizeConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        // System.Data.SQLite compatibility: Microsoft.Data.Sqlite does not support "Version".
        if (builder.ContainsKey("Version"))
            builder.Remove("Version");

        // Legacy connection strings may include unsupported journal mode keys.
        if (builder.ContainsKey("Journal Mode"))
            builder.Remove("Journal Mode");

        if (builder.ContainsKey("JournalMode"))
            builder.Remove("JournalMode");

        return builder.ConnectionString;
    }
}
