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
    /// <summary>Statement run after the query (also on failure) to clear session-scoped security state; empty when settings are transaction-local.</summary>
    CompiledCommand CompileSessionTeardown(IReadOnlyList<KeyValuePair<string, string?>> settings) => new("", []);
}

public sealed record CompiledCommand(string CommandText, ImmutableArray<QueryParameter> Parameters);

public sealed record QueryParameter(string Name, object? Value);

public enum ResultRole
{
    /// <summary>Written to the response.</summary>
    Output,
    /// <summary>Fetched only to build the next cursor.</summary>
    Cursor,
    /// <summary>Target key of a join: null means the related row is absent, so the nested object is written as null.</summary>
    JoinKey,
}

/// <summary>One SELECT-list entry. Compilers must emit exactly <see cref="For"/>'s columns, in order.</summary>
public sealed record ResultColumn(ResolvedField Field, ResultRole Role)
{
    public static ImmutableArray<ResultColumn> For(ResolvedQuery q)
    {
        var columns = q.Select.Select(f => new ResultColumn(f, ResultRole.Output)).ToList();
        if (q.CursorCapable)
            foreach (var o in q.OrderBy.Where(o => !columns.Any(c => Same(c.Field, o.Field))))
                columns.Add(new ResultColumn(o.Field, ResultRole.Cursor));
        foreach (var join in q.Joins.Where(j => q.Select.Any(f => f.Path.StartsWith(j.Path + ".", StringComparison.Ordinal))))
            columns.Add(new ResultColumn(new ResolvedField(join.Alias, join.Path, join.Target.Key[0]), ResultRole.JoinKey));
        return [.. columns];
    }

    public static bool Same(ResolvedField a, ResolvedField b) => a.Alias == b.Alias && a.Column == b.Column;
}

public sealed record QueryPlan(string CommandText, ImmutableArray<QueryParameter> Parameters, ImmutableArray<ResultColumn> ResultColumns)
{
    public CompiledCommand Command => new(CommandText, Parameters);
}
