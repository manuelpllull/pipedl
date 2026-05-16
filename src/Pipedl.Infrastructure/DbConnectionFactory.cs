using System.Data;
using System.Data.SQLite;

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
        return new SQLiteConnection(_connectionString);
    }
}
