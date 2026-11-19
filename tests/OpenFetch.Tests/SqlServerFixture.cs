using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OpenFetch.EntityFrameworkCore;
using OpenFetch.Tests.Domain;
using Testcontainers.MsSql;

namespace OpenFetch.Tests;

/// <summary>Seeded SQL Server 2022 with the sample security policy. Every OpenFetch query here runs as the RLS-bound app login.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string AppConnectionString(int maxPoolSize = 10) => new SqlConnectionStringBuilder(_container.GetConnectionString())
    {
        UserID = "openfetch_app",
        Password = "OpenFetch_app_1!",
        MaxPoolSize = maxPoolSize,
        TrustServerCertificate = true,
    }.ConnectionString;

    public static AppDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>The sample model read through the SQL Server EF provider (store types differ from PostgreSQL).</summary>
    public static readonly IReadOnlyList<EntityMapping> Mappings =
        [.. EfCoreModelReader.Read(CreateDbContext("Server=unused").Model)];

    public static QueryModel Model(Action<OpenFetchOptions>? configure = null)
    {
        var options = new OpenFetchOptions();
        configure?.Invoke(options);
        return QueryModelBuilder.Build(Mappings, options);
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using (var db = CreateDbContext(_container.GetConnectionString()))
        {
            await db.Database.EnsureCreatedAsync();
            SeedData.Seed(db);
        }
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "sqlserver-rls.sql"));
        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
