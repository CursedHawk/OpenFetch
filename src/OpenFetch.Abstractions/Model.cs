using System.Collections.Frozen;
using System.Collections.Immutable;

namespace OpenFetch;

/// <summary>The authoritative, immutable model (§9). Built once at startup; every other component reads it, none rediscover EF metadata.</summary>
public sealed record QueryModel(FrozenDictionary<string, DatasetDescriptor> Datasets, QueryLimits Limits, QueryFeatures Features, string Version)
{
    private readonly FrozenDictionary<Type, DatasetDescriptor> _byType = Datasets.Values.ToFrozenDictionary(d => d.ClrType);

    public DatasetDescriptor? FindByClrType(Type type) => _byType.GetValueOrDefault(type);
}

public sealed record QueryFeatures
{
    public bool FieldComparison { get; init; }
    /// <summary>
    /// Registered extension properties (spec §11): <c>x-&lt;vendor&gt;-&lt;name&gt;</c> at the document root. They are accepted and
    /// handed to the application untouched; the engine never interprets them. Unregistered extensions are rejected.
    /// </summary>
    public ImmutableArray<string> Extensions { get; init; } = [];
}

public sealed record DatasetDescriptor
{
    public required string PublicName { get; init; }
    public required Type ClrType { get; init; }
    public string? Description { get; init; }
    public required int DefaultPageSize { get; init; }
    public required int MaximumPageSize { get; init; }
    public required int MaximumRelationshipDepth { get; init; }
    public required DatasetCapabilities Capabilities { get; init; }
    public ImmutableArray<string> Policies { get; init; } = [];
    public bool Deprecated { get; init; }
    public string? DeprecationMessage { get; init; }

    /// <summary>Exposed columns by public name.</summary>
    public required FrozenDictionary<string, ColumnDescriptor> Columns { get; init; }
    /// <summary>Every mapped scalar property, exposed or not, in declaration order. Trusted code only (policies, joins, keys).</summary>
    public required ImmutableArray<ColumnDescriptor> AllColumns { get; init; }
    public required FrozenDictionary<string, RelationshipDescriptor> Relationships { get; init; }
    /// <summary>Primary key columns: the cursor tie-breaker.</summary>
    public required ImmutableArray<ColumnDescriptor> Key { get; init; }
    public ImmutableArray<RowPolicy> RowPolicies { get; init; } = [];
    /// <summary>Named fixed projections (§18.5): name -> selected field paths, served as <c>POST /query/{dataset}/{name}</c>.</summary>
    public FrozenDictionary<string, ImmutableArray<string>> Projections { get; init; } = FrozenDictionary<string, ImmutableArray<string>>.Empty;

    /// <summary>Internal physical identifiers; never serialized into schema, capabilities, OpenAPI or errors.</summary>
    public required PhysicalTable Table { get; init; }
    /// <summary>TPT: base-type tables joined 1:1 on the key; columns with <see cref="PhysicalColumn.Table"/> = i live in entry i-1.</summary>
    public ImmutableArray<InheritedTable> InheritedTables { get; init; } = [];
    /// <summary>TPC: the dataset reads UNION ALL over these concrete tables (column names are aliased to the property names).</summary>
    public ImmutableArray<UnionBranch> Union { get; init; } = [];
    /// <summary>Reachable only as a collection of another dataset (EF owned collections); never queried directly.</summary>
    public bool Internal { get; init; }

    public IEnumerable<ColumnDescriptor> ExposedColumns => AllColumns.Where(c => c.Exposed);

    public ColumnDescriptor? FindProperty(string propertyName) => AllColumns.FirstOrDefault(c => c.PropertyName == propertyName);
}

public sealed record ColumnDescriptor
{
    /// <summary>Public name; equals <see cref="PropertyName"/> for unexposed columns.</summary>
    public required string PublicName { get; init; }
    public required string PropertyName { get; init; }
    /// <summary>Non-nullable CLR type of the property.</summary>
    public required Type ClrType { get; init; }
    public required ScalarKind Kind { get; init; }
    public required bool Nullable { get; init; }
    public required bool Exposed { get; init; }
    public ColumnCapabilities Capabilities { get; init; }
    public FrozenSet<QueryOperator> Operators { get; init; } = FrozenSet<QueryOperator>.Empty;
    public ImmutableArray<string> Policies { get; init; } = [];
    public string? Description { get; init; }
    public Sensitivity Sensitivity { get; init; }
    public bool Deprecated { get; init; }
    public string? DeprecationMessage { get; init; }
    public string? Format { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    /// <summary>Leading column of an index or key; feeds complexity scoring.</summary>
    public bool Indexed { get; init; }
    /// <summary>Public enum member names (camelCase), in declaration order; empty for non-enums.</summary>
    public ImmutableArray<string> EnumValues { get; init; } = [];
    public required PhysicalColumn Physical { get; init; }

    public bool Can(ColumnCapabilities capability) => Exposed && Capabilities.HasFlag(capability);
}

public enum Cardinality { One, Many }

public sealed record RelationshipDescriptor
{
    public required string PublicName { get; init; }
    public required string PropertyName { get; init; }
    public required string TargetDataset { get; init; }
    public required Cardinality Cardinality { get; init; }
    public required RelationshipCapabilities Capabilities { get; init; }
    public required int MaximumDepth { get; init; }
    public ImmutableArray<string> Policies { get; init; } = [];
    public string? Description { get; init; }
    public bool Deprecated { get; init; }
    public string? DeprecationMessage { get; init; }
    /// <summary>Equi-join pairs (source column = target column). Always LEFT JOIN.</summary>
    public required ImmutableArray<(ColumnDescriptor Source, ColumnDescriptor Target)> Join { get; init; }

    public bool Can(RelationshipCapabilities capability) =>
        Capabilities.HasFlag(RelationshipCapabilities.Expand) && Capabilities.HasFlag(capability);
}

public sealed record PhysicalTable(string? Schema, string Name);

/// <summary>A table joined 1:1 to the dataset's main table on its key (TPT inheritance). Pairs are (main column, joined column).</summary>
public sealed record InheritedTable(PhysicalTable Table, ImmutableArray<(string Main, string Joined)> Key);

/// <summary>One branch of a TPC union: maps each dataset column (by its <see cref="PhysicalColumn.Name"/>) to this table's column.</summary>
public sealed record UnionBranch(PhysicalTable Table, FrozenDictionary<string, string> Columns);

/// <summary>Physical column with conversion between the public CLR value and the provider value (EF value converters, enums).</summary>
public sealed record PhysicalColumn(
    string Name,
    string? StoreType,
    Type ProviderClrType,
    Func<object?, object?>? ToProvider,
    Func<object?, object?>? FromProvider,
    int Table = 0);

/// <summary>A trusted, server-built predicate template. <see cref="ContextType"/> is resolved from DI per request.</summary>
public sealed record RowPolicy(Type ContextType, Func<object, FilterNode> Build);
