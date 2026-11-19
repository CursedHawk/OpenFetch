namespace OpenFetch;

/// <summary>Opts a CLR entity in as a public dataset (§7.2). Without this (or fluent <c>Expose</c>) nothing is exposed.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class QueryDatasetAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string? Description { get; set; }
    /// <summary>0 = use <see cref="QueryLimits.DefaultPageSize"/>.</summary>
    public int DefaultPageSize { get; set; }
    /// <summary>0 = use <see cref="QueryLimits.MaximumPageSize"/>.</summary>
    public int MaximumPageSize { get; set; }
    public string? AuthorizationPolicy { get; set; }
    /// <summary>0 = use <see cref="QueryLimits.MaximumRelationshipDepth"/>.</summary>
    public int MaximumRelationshipDepth { get; set; }
    public DatasetCapabilities Capabilities { get; set; } = DatasetCapabilities.Default;
    public bool Deprecated { get; set; }
    public string? DeprecationMessage { get; set; }
}

/// <summary>Opts a scalar property in as a public column (§7.3). Applying the attribute enables selection only unless more is set.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class QueryColumnAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public string? Description { get; set; }
    public bool Select { get; set; } = true;
    public bool Filter { get; set; }
    public bool Sort { get; set; }
    public bool Group { get; set; }
    public bool Aggregate { get; set; }
    public bool Cursor { get; set; }
    /// <summary>Empty = the type's default operator set (<see cref="QueryOperators.DefaultsFor"/>).</summary>
    public QueryOperator[] AllowedOperators { get; set; } = [];
    public string? AuthorizationPolicy { get; set; }
    public Sensitivity Sensitivity { get; set; }
    public bool Deprecated { get; set; }
    public string? DeprecationMessage { get; set; }
    /// <summary>Public format override (OpenAPI <c>format</c>).</summary>
    public string? Format { get; set; }
    /// <summary>0 = take from EF metadata.</summary>
    public int MaximumLength { get; set; }
    /// <summary>0 = take from EF metadata.</summary>
    public int Precision { get; set; }
}

/// <summary>Exposes a navigation as a logical relationship (§7.5). Join metadata always comes from EF, never from clients.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class QueryRelationshipAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public string? Description { get; set; }
    /// <summary>Master switch: the relationship may be joined at all.</summary>
    public bool Expand { get; set; } = true;
    public bool Select { get; set; } = true;
    public bool Filter { get; set; }
    public bool Sort { get; set; }
    /// <summary>0 = dataset/global default.</summary>
    public int MaximumDepth { get; set; }
    public string? AuthorizationPolicy { get; set; }
    public bool Deprecated { get; set; }
    public string? DeprecationMessage { get; set; }
}

/// <summary>Absolute deny (§7.4): the member is never exposed, whatever other configuration says. Trusted row policies may still use it.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
public sealed class QueryHiddenAttribute : Attribute;

public enum Sensitivity { Public, Internal, Confidential, Restricted }

[Flags]
public enum DatasetCapabilities
{
    None = 0,
    Filter = 1,
    Sort = 2,
    CursorPagination = 4,
    OffsetPagination = 8,
    Distinct = 16,
    /// <summary>groupBy/aggregates/having (§21). Opt-in: counts and sums disclose more than rows do.</summary>
    Aggregation = 32,
    Default = Filter | Sort | CursorPagination | OffsetPagination,
}

[Flags]
public enum ColumnCapabilities
{
    None = 0,
    Select = 1,
    Filter = 2,
    Sort = 4,
    Group = 8,
    Aggregate = 16,
    Cursor = 32,
}

[Flags]
public enum RelationshipCapabilities
{
    None = 0,
    Expand = 1,
    Select = 2,
    Filter = 4,
    Sort = 8,
}
