using Microsoft.EntityFrameworkCore;

namespace OpenFetch.Tests.Domain;

public enum OrderStatus { Open, Processing, Shipped, Cancelled }

[QueryDataset("customers", Description = "Customers")]
public sealed class Customer
{
    [QueryColumn("id", Filter = true, Sort = true, Cursor = true)]
    public Guid Id { get; set; }

    [QueryColumn("name", Filter = true, Sort = true)]
    public required string Name { get; set; }

    [QueryColumn("country", Filter = true, Sort = true)]
    public string? Country { get; set; }

    [QueryHidden]
    public Guid TenantId { get; set; }

    public List<Order> Orders { get; set; } = [];
}

[QueryDataset("orders", Description = "Queryable sales orders", DefaultPageSize = 100, MaximumPageSize = 500)]
public sealed class Order
{
    [QueryColumn("id", Filter = true, Sort = true, Cursor = true)]
    public Guid Id { get; set; }

    [QueryColumn("number", Filter = true, Sort = true,
        AllowedOperators = [QueryOperator.Equal, QueryOperator.NotEqual, QueryOperator.Contains, QueryOperator.StartsWith])]
    public required string Number { get; set; }

    [QueryColumn("status", Filter = true, Sort = true,
        AllowedOperators = [QueryOperator.Equal, QueryOperator.NotEqual, QueryOperator.In])]
    public OrderStatus Status { get; set; }

    [QueryColumn("total", Filter = true, Sort = true, Aggregate = true, AuthorizationPolicy = "Orders.FinancialData")]
    public decimal Total { get; set; }

    [QueryColumn("createdAt", Filter = true, Sort = true, Cursor = true)]
    public DateTimeOffset CreatedAt { get; set; }

    [QueryHidden]
    public Guid TenantId { get; set; }

    public bool IsDeleted { get; set; }

    public Guid CustomerId { get; set; }

    [QueryRelationship("customer", Select = true, Filter = true, Sort = true, MaximumDepth = 1)]
    public Customer Customer { get; set; } = null!;
}

/// <summary>Sample domain mirroring spec §7.3 / §19.2: sales.Orders -> crm.Customers, two tenants.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.ToTable("Customers", "crm");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(2);
        });
        b.Entity<Order>(e =>
        {
            e.ToTable("Orders", "sales");
            e.Property(x => x.Number).HasMaxLength(32);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
    }
}

public static class SeedData
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public static void Seed(AppDbContext db)
    {
        foreach (var (tenant, prefix, country) in new[] { (TenantA, "A", "CZ"), (TenantB, "B", "SK") })
        {
            var customers = Enumerable.Range(1, 3).Select(i => new Customer
            {
                Id = Id(tenant, 100 + i), Name = $"Customer {prefix}{i}", Country = i == 3 ? null : country, TenantId = tenant,
            }).ToList();
            db.Customers.AddRange(customers);
            db.Orders.AddRange(Enumerable.Range(1, 10).Select(i => new Order
            {
                Id = Id(tenant, i),
                Number = $"SO-{prefix}-{i:D4}",
                Status = (OrderStatus)(i % 4),
                Total = 100m * i + 0.5m,
                CreatedAt = new DateTimeOffset(2026, 1, i, 12, 0, 0, TimeSpan.Zero),
                TenantId = tenant,
                IsDeleted = i == 10,
                Customer = customers[i % 3],
            }));
        }
        db.SaveChanges();
    }

    // Deterministic ids: tenant prefix + sequence, so tests can reference rows directly.
    public static Guid Id(Guid tenant, int n) => Guid.Parse($"{tenant.ToString()[..8]}-1111-0000-0000-{n:D12}");
}
