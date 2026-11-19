using Microsoft.EntityFrameworkCore;
using OpenFetch.EntityFrameworkCore;

namespace OpenFetch.Tests;

public class EfCoreReaderTests
{
    [QueryDataset("animals")] public class Animal { [QueryColumn] public int Id { get; set; } }
    [QueryDataset("dogs")] public class Dog : Animal { [QueryColumn] public string? Breed { get; set; } }
    public class Address { public string? City { get; set; } }
    [QueryDataset("people")] public class Person
    {
        [QueryColumn] public int Id { get; set; }
        public Address Home { get; set; } = new();
        public bool Archived { get; set; }
    }
    public class Plain { public int Id { get; set; } public string? Secret { get; set; } }

    private sealed class EdgeContext(DbContextOptions<EdgeContext> o) : DbContext(o)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Animal>();
            b.Entity<Dog>();
            b.Entity<Person>().OwnsOne(p => p.Home);
            b.Entity<Person>().HasQueryFilter(p => !p.Archived);
            b.Entity<Plain>().Property<int>("ShadowValue");
        }
    }

    private static List<EntityMapping> Read() =>
        [.. EfCoreModelReader.Read(new EdgeContext(new DbContextOptionsBuilder<EdgeContext>().UseNpgsql("Host=unused").Options).Model)];

    [Fact]
    public void Unsupported_shapes_are_flagged_and_fail_only_when_exposed()
    {
        var mappings = Read();
        Assert.Contains("inheritance", mappings.Single(m => m.ClrType == typeof(Dog)).UnsupportedReason);
        Assert.Contains("owned", mappings.Single(m => m.ClrType == typeof(Address)).UnsupportedReason);

        var ex = Assert.Throws<OpenFetchConfigurationException>(() => QueryModelBuilder.Build(mappings, new OpenFetchOptions()));
        Assert.Contains(ex.Errors, e => e.Contains("'dogs'") && e.Contains("inheritance"));
        Assert.DoesNotContain(ex.Errors, e => e.Contains("Address")); // never exposed, never an error
    }

    [Fact]
    public void Query_filter_warns_and_unattributed_entities_are_invisible()
    {
        var mappings = Read().Where(m => m.ClrType == typeof(Person) || m.ClrType == typeof(Plain) || m.ClrType == typeof(Address)).ToList();
        Assert.True(mappings.Single(m => m.ClrType == typeof(Plain)).Properties.Single(p => p.Name == "ShadowValue").IsShadow);

        // Fail closed: an exposed entity with an EF query filter needs a row policy or an explicit acknowledgement.
        var ex = Assert.Throws<OpenFetchConfigurationException>(() => QueryModelBuilder.Build(mappings, new OpenFetchOptions()));
        Assert.Contains(ex.Errors, e => e.Contains("global query filter"));

        var options = new OpenFetchOptions();
        options.Dataset<Person>().ApplyRowPolicy(p => !p.Archived);
        var warnings = new List<string>();
        var model = QueryModelBuilder.Build(mappings, options, warnings);
        Assert.Equal(["people"], model.Datasets.Keys);
        Assert.Equal(["id"], model.Datasets["people"].Columns.Keys);
        Assert.Contains(warnings, w => w.Contains("global query filter"));
    }
}
