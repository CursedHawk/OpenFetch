using System.Collections.Immutable;

namespace OpenFetch;

/// <summary>Physical metadata for one entity, produced by an ORM adapter (EF Core). The input to <see cref="QueryModelBuilder"/>.</summary>
public sealed record EntityMapping
{
    public required Type ClrType { get; init; }
    public required PhysicalTable Table { get; init; }
    public required ImmutableArray<PropertyMapping> Properties { get; init; }
    /// <summary>Primary-key property names; empty for keyless entities.</summary>
    public ImmutableArray<string> Key { get; init; } = [];
    public ImmutableArray<NavigationMapping> Navigations { get; init; } = [];
    /// <summary>Property names that lead an index.</summary>
    public ImmutableHashSet<string> IndexedProperties { get; init; } = [];
    /// <summary>The ORM applies a global query filter that OpenFetch's direct SQL will not.</summary>
    public bool HasQueryFilter { get; init; }
    /// <summary>Set when the entity's shape is unsupported (SQL-query mapped, unmapped, ...). Exposing it fails startup.</summary>
    public string? UnsupportedReason { get; init; }
    /// <summary>TPT base tables joined on the key.</summary>
    public ImmutableArray<InheritedTable> InheritedTables { get; init; } = [];
    /// <summary>TPC concrete tables combined with UNION ALL.</summary>
    public ImmutableArray<UnionBranch> Union { get; init; } = [];
    /// <summary>TPH: discriminator property and the values of this type and its subtypes (a trusted predicate).</summary>
    public (string Property, ImmutableArray<object> Values)? TypeFilter { get; init; }
    /// <summary>EF owned collection (own table): only reachable as a collection relationship of its owner.</summary>
    public bool IsOwnedCollection { get; init; }
    /// <summary>Optional owned references flattened into this entity, by property path ("Home", "Home.Geo").</summary>
    public ImmutableArray<string> OptionalOwned { get; init; } = [];
}

public sealed record PropertyMapping
{
    public required string Name { get; init; }
    public required Type ClrType { get; init; }
    public required bool Nullable { get; init; }
    public required PhysicalColumn Column { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    /// <summary>No CLR property (EF shadow property): usable by joins, never exposable.</summary>
    public bool IsShadow { get; init; }
    /// <summary>Owned (table-split) properties: dotted name "Home.City"; attributes are read from this type.</summary>
    public Type? DeclaringType { get; init; }
}

/// <summary>A navigation with its equi-join columns as (source property, target property) pairs.</summary>
public sealed record NavigationMapping(string Name, Type TargetClrType, bool IsCollection, ImmutableArray<(string Source, string Target)> Join);
