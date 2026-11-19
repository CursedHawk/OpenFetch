using System.Collections.Immutable;

namespace OpenFetch;

/// <summary>A query whose every name points to trusted metadata (§4). After security it also carries the mandatory policy (the "secured query").</summary>
public sealed record ResolvedQuery
{
    public required DatasetDescriptor Dataset { get; init; }
    /// <summary>Root table alias is always <c>t0</c>; joins are <c>t1..tn</c> in resolution order.</summary>
    public ImmutableArray<ResolvedJoin> Joins { get; init; } = [];
    public required ImmutableArray<ResolvedField> Select { get; init; }
    public ResolvedFilter? Where { get; init; }
    /// <summary>Trusted root policy, AND-ed outside the client filter so it cannot be negated or escaped.</summary>
    public ResolvedFilter? Policy { get; init; }
    /// <summary>Client ordering followed by the key tie-breaker.</summary>
    public required ImmutableArray<ResolvedOrder> OrderBy { get; init; }
    public required int PageSize { get; init; }
    public int Offset { get; init; }
    /// <summary>Keyset position from a validated cursor: one value per <see cref="OrderBy"/> entry.</summary>
    public ImmutableArray<object?>? After { get; init; }
    public bool Distinct { get; init; }
    /// <summary>True when every order field can be encoded in a cursor (root, non-null, cursor-eligible or key).</summary>
    public bool CursorCapable { get; init; }
}

public sealed record ResolvedJoin(string Alias, string ParentAlias, string Path, RelationshipDescriptor Relationship, DatasetDescriptor Target)
{
    /// <summary>Target dataset's trusted policy, placed in the ON clause so filtered-out rows surface as null.</summary>
    public ResolvedFilter? Policy { get; init; }
}

/// <summary>A column on a specific table alias. <see cref="Path"/> is the logical output path (<c>customer.name</c>).</summary>
public sealed record ResolvedField(string Alias, string Path, ColumnDescriptor Column);

public sealed record ResolvedOrder(ResolvedField Field, SortDirection Direction);

public abstract record ResolvedFilter;
public sealed record ResolvedLogical(LogicalOperator Operator, ImmutableArray<ResolvedFilter> Children) : ResolvedFilter;
public sealed record ResolvedNot(ResolvedFilter Operand) : ResolvedFilter;
public sealed record ResolvedComparison(ResolvedField Field, QueryOperator Operator, ResolvedOperand Right) : ResolvedFilter;

/// <summary>Operand values are already converted to the column's public CLR type; the compiler applies provider conversion.</summary>
public abstract record ResolvedOperand;
public sealed record ResolvedConstant(object? Value) : ResolvedOperand;
public sealed record ResolvedList(ImmutableArray<object> Values) : ResolvedOperand;
public sealed record ResolvedFieldOperand(ResolvedField Field) : ResolvedOperand;

/// <summary>Provider compiler contract (§19.1).</summary>
public interface IQueryCompiler
{
    string ProviderName { get; }
    string CompilerVersion { get; }
    QueryPlan Compile(ResolvedQuery query);
    /// <summary>Statement run first in the query's transaction: read-only mode plus transaction-local RLS settings.</summary>
    CompiledCommand CompileSessionSetup(IReadOnlyList<KeyValuePair<string, string?>> settings);
}

public sealed record CompiledCommand(string CommandText, ImmutableArray<QueryParameter> Parameters);

public sealed record QueryParameter(string Name, object? Value);

/// <summary><see cref="Emit"/> false = fetched only for cursor construction.</summary>
public sealed record ResultColumn(string Path, ColumnDescriptor Column, bool Emit);

public sealed record QueryPlan(string CommandText, ImmutableArray<QueryParameter> Parameters, ImmutableArray<ResultColumn> ResultColumns)
{
    public CompiledCommand Command => new(CommandText, Parameters);
}
