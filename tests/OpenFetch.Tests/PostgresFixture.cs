using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenFetch.Tests.Domain;
using Testcontainers.PostgreSql;

namespace OpenFetch.Tests;

/// <summary>One seeded PostgreSQL container with the sample RLS policies, shared by every test in the "postgres" collection.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Owner/superuser connection: bypasses RLS. Only for setup and app-policy-only tests.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Non-BYPASSRLS application role; RLS applies.</summary>
    public string AppConnectionString(int maxPoolSize = 10) => new NpgsqlConnectionStringBuilder(ConnectionString)
    {
        Username = "openfetch_app",
        Password = "openfetch_app",
        MaxPoolSize = maxPoolSize,
    }.ConnectionString;

    public AppDbContext CreateDbContext(string? connectionString = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString ?? ConnectionString).Options);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        SeedData.Seed(db);
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "postgresql-rls.sql")));
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
