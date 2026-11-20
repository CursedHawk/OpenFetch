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
    public void Hierarchies_and_owned_types_are_read_not_rejected()
    {
        var mappings = Read();
        var dog = mappings.Single(m => m.ClrType == typeof(Dog));
        Assert.Null(dog.UnsupportedReason);
        Assert.Equal("Discriminator", dog.TypeFilter!.Value.Property);
        Assert.Equal(["Dog"], dog.TypeFilter.Value.Values);
        Assert.Null(mappings.Single(m => m.ClrType == typeof(Animal)).TypeFilter); // the root sees every row
        Assert.DoesNotContain(mappings, m => m.ClrType == typeof(Address));       // owned reference: flattened into its owner
        Assert.Contains(mappings.Single(m => m.ClrType == typeof(Person)).Properties, p => p.Name == "Home.City" && p.DeclaringType == typeof(Address));

        var model = QueryModelBuilder.Build(mappings, new OpenFetchOptions().Configure(o => o.Dataset<Person>().AcknowledgeEfQueryFilterNotApplied()));
        Assert.Equal(["animals", "dogs", "people"], model.Datasets.Keys.Order());
        Assert.Single(model.Datasets["dogs"].RowPolicies);
        Assert.DoesNotContain("home.city", model.Datasets["people"].Columns.Keys); // owner navigation not opted in
    }

    [Fact]
    public void Base_type_policies_and_owner_gates_flow_to_derived_and_owned_members()
    {
        var mappings = EfCoreModelReader.Read(new Domain.ShapesContext(new DbContextOptionsBuilder<Domain.ShapesContext>().UseNpgsql("Host=unused").Options).Model).ToList();
        var options = new OpenFetchOptions();
        options.Dataset<Domain.Animal>().RequirePolicy("Animals.Read").ApplyRowPolicy(a => a.Name != "hidden");
        options.Dataset<Domain.Person>().RequirePolicy("People.Read").Relationship(p => p.Home).RequirePolicy("Pii");
        var model = QueryModelBuilder.Build(mappings, options);

        var dogs = model.Datasets["dogs"];
        Assert.Contains("Animals.Read", dogs.Policies);
        Assert.Equal(2, dogs.RowPolicies.Length); // inherited Animal policy + TPH type filter
        Assert.Contains("Pii", model.Datasets["people"].Columns["home.city"].Policies);
        Assert.False(model.Datasets["people"].Columns["home.zip"].Can(ColumnCapabilities.Filter)); // owned column without Filter stays unfilterable
        Assert.Contains("People.Read", model.Datasets["phone"].Policies); // internal dataset inherits its owner's policy
    }

    public class Secret
    {
        [QueryColumn("note", AuthorizationPolicy = "Pii")] public string? Note { get; set; }
        [QueryColumn("label")] public string? Label { get; set; }
    }
    [QueryDataset("vaults")] public class Vault
    {
        [QueryColumn("id")] public int Id { get; set; }
        [QueryRelationship("secret")] public Secret? Secret { get; set; }
    }
    private sealed class VaultContext(DbContextOptions<VaultContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b) => b.Entity<Vault>().OwnsOne(v => v.Secret);
    }

    [Fact]
    public async Task Optional_owned_presence_reads_only_columns_the_caller_may_see()
    {
        var model = QueryModelBuilder.Build(EfCoreModelReader.Read(new VaultContext(new DbContextOptionsBuilder<VaultContext>().UseNpgsql("Host=unused").Options).Model), new OpenFetchOptions());
        Assert.Equal(["secret.label", "secret.note"], model.Datasets["vaults"].OptionalOwned["secret"].Select(c => c.PublicName));

        var engine = TestEngine.Create(model);
        var withoutPii = await engine.PrepareAsync("vaults", """{"select":["secret.label"]}""", TestEngine.Context(Domain.SeedData.TenantA, []));
        Assert.Contains("CASE WHEN t0.\"Secret_Label\" IS NOT NULL THEN 1 END", withoutPii.Plan.CommandText);
        Assert.DoesNotContain("Secret_Note", withoutPii.Plan.CommandText); // a field the caller cannot read never decides whether the object exists
        var withPii = await engine.PrepareAsync("vaults", """{"select":["secret.label"]}""", TestEngine.Context(Domain.SeedData.TenantA, ["Pii"]));
        Assert.Contains("t0.\"Secret_Label\" IS NOT NULL OR t0.\"Secret_Note\" IS NOT NULL", withPii.Plan.CommandText);
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
