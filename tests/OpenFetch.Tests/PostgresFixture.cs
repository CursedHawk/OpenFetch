using Microsoft.EntityFrameworkCore;
using OpenFetch.Tests.Domain;
using Testcontainers.PostgreSql;

namespace OpenFetch.Tests;

/// <summary>One seeded PostgreSQL container shared by every test in the "postgres" collection.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        SeedData.Seed(db);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
