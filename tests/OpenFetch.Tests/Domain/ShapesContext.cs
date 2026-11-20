using Microsoft.EntityFrameworkCore;

namespace OpenFetch.Tests.Domain;

// Inheritance (TPH/TPT/TPC) and owned types (OwnsOne table-split, OwnsMany) exposed through OpenFetch.

[QueryDataset("animals")]
public class Animal
{
    [QueryColumn("id", Filter = true, Sort = true, Cursor = true)] public int Id { get; set; }
    [QueryColumn("name", Filter = true, Sort = true)] public string Name { get; set; } = "";
}
[QueryDataset("dogs")] public class Dog : Animal { [QueryColumn("breed", Filter = true)] public string? Breed { get; set; } }
public class Cat : Animal { public int Lives { get; set; } }

[QueryDataset("vehicles")]
public class Vehicle
{
    [QueryColumn("id", Filter = true, Sort = true)] public int Id { get; set; }
    [QueryColumn("make", Filter = true, Sort = true)] public string Make { get; set; } = "";
}
[QueryDataset("cars")] public class Car : Vehicle { [QueryColumn("doors", Filter = true)] public int Doors { get; set; } }

[QueryDataset("payments")]
public abstract class Payment
{
    [QueryColumn("id", Filter = true, Sort = true)] public int Id { get; set; }
    [QueryColumn("amount", Filter = true, Sort = true)] public decimal Amount { get; set; }
}
[QueryDataset("cardPayments")] public class CardPayment : Payment { [QueryColumn("last4")] public string Last4 { get; set; } = ""; }
public class BankPayment : Payment { public string Iban { get; set; } = ""; }

public class Address
{
    [QueryColumn("city", Filter = true, Sort = true)] public string City { get; set; } = "";
    [QueryColumn("zip")] public string Zip { get; set; } = "";
}
public class Phone
{
    [QueryColumn("number", Filter = true)] public string Number { get; set; } = "";
    [QueryColumn("kind", Filter = true, Sort = true)] public string Kind { get; set; } = "";
}

[QueryDataset("people", Capabilities = DatasetCapabilities.Default | DatasetCapabilities.Distinct)]
public class Person
{
    [QueryColumn("id", Filter = true, Sort = true)] public int Id { get; set; }
    [QueryColumn("name", Filter = true, Sort = true)] public string Name { get; set; } = "";
    [QueryRelationship("home", Filter = true, Sort = true)] public Address? Home { get; set; }
    [QueryRelationship("phones", Filter = true)] public List<Phone> Phones { get; set; } = [];
}

public sealed class ShapesContext(DbContextOptions<ShapesContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Animal>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Dog>();
        b.Entity<Cat>();
        b.Entity<Vehicle>().UseTptMappingStrategy().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Car>().ToTable("Cars");
        b.Entity<Payment>().UseTpcMappingStrategy().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<CardPayment>().ToTable("CardPayments").Property(x => x.Amount).HasPrecision(18, 2);
        b.Entity<BankPayment>().ToTable("BankPayments").Property(x => x.Amount).HasPrecision(18, 2);
        b.Entity<Person>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.OwnsOne(x => x.Home);
            e.OwnsMany(x => x.Phones, p => p.ToTable("Phones"));
        });
    }

    public static void Seed(ShapesContext db)
    {
        db.AddRange(
            new Animal { Id = 1, Name = "Generic" }, new Dog { Id = 2, Name = "Rex", Breed = "Lab" },
            new Cat { Id = 3, Name = "Tom", Lives = 9 }, new Dog { Id = 4, Name = "Fido", Breed = "Pug" },
            new Vehicle { Id = 1, Make = "Generic" }, new Car { Id = 2, Make = "Skoda", Doors = 5 },
            new CardPayment { Id = 1, Amount = 10.5m, Last4 = "4242" }, new BankPayment { Id = 2, Amount = 20m, Iban = "CZ65" },
            new Person { Id = 1, Name = "Ann", Home = new() { City = "Prague", Zip = "11000" }, Phones = [new() { Number = "+420 1", Kind = "mobile" }, new() { Number = "+420 2", Kind = "work" }] },
            new Person { Id = 2, Name = "Bob", Home = new() { City = "Brno", Zip = "60200" } },
            new Person { Id = 3, Name = "Cyril" }); // no home: an absent optional owned reference
        db.SaveChanges();
    }
}
