using System.Collections.Immutable;

namespace OpenFetch;

/// <summary>Canonical, provider-neutral syntax tree (§15). Field names are logical paths (<c>customer.name</c>); no SQL anywhere.</summary>
public sealed record QueryDocument(
    string Version,
    ImmutableArray<FieldRef> Select,
    FilterNode? Where,
    ImmutableArray<OrderNode> OrderBy,
    PageNode Page,
    bool Distinct)
{
    /// <summary>Aggregation (§21): group keys. Output rows contain the group fields followed by the aggregate aliases.</summary>
    public ImmutableArray<FieldRef> GroupBy { get; init; } = [];
    public ImmutableArray<AggregateNode> Aggregates { get; init; } = [];
    /// <summary>Filter on aggregate aliases and group fields, applied after grouping.</summary>
    public FilterNode? Having { get; init; }

    public bool IsAggregate => !GroupBy.IsEmpty || !Aggregates.IsEmpty;

    /// <summary>Collection relationships returned as nested arrays per row.</summary>
    public ImmutableArray<ExpandNode> Expand { get; init; } = [];
}

/// <summary><c>{"relationship":"orders","select":[...],"where":{...},"orderBy":[...],"first":10}</c> on a one-to-many relationship.</summary>
public sealed record ExpandNode(string Relationship, ImmutableArray<FieldRef> Select, FilterNode? Where, ImmutableArray<OrderNode> OrderBy, int? First, string Pointer);

public enum AggregateFunction { Count, CountDistinct, Sum, Avg, Min, Max }

/// <summary><c>{"function":"sum","field":"total","alias":"revenue"}</c>; <c>count</c> may omit the field (counts rows).</summary>
public sealed record AggregateNode(AggregateFunction Function, FieldRef? Field, string Alias, string Pointer);

/// <summary>A logical field path plus the JSON Pointer it came from (for error reporting).</summary>
public sealed record FieldRef(string Path, string Pointer = "")
{
    public override string ToString() => Path;
}

public enum SortDirection { Ascending, Descending }

public sealed record OrderNode(FieldRef Field, SortDirection Direction);

/// <summary><c>first</c> is the page size; <c>after</c> (cursor) and <c>offset</c> are mutually exclusive.</summary>
public sealed record PageNode(int? First, string? After, int? Offset);

public enum LogicalOperator { And, Or }

public abstract record FilterNode
{
    public string Pointer { get; init; } = "";
}

public sealed record LogicalFilter(LogicalOperator Operator, ImmutableArray<FilterNode> Children) : FilterNode;

public sealed record NotFilter(FilterNode Operand) : FilterNode;

/// <summary><c>{"type":"any","relationship":"orders","where":{...}}</c>: true when at least one related row matches (EXISTS).
/// "none" is <c>not any</c>; "all" is <c>not any(not ...)</c>.</summary>
public sealed record AnyFilter(string Relationship, FilterNode? Where) : FilterNode;

public sealed record ComparisonFilter(FieldRef Left, QueryOperator Operator, QueryOperand Right) : FilterNode;

public abstract record QueryOperand;
public sealed record ConstantOperand(QueryValue Value) : QueryOperand;
public sealed record ListOperand(ImmutableArray<QueryValue> Values) : QueryOperand;
/// <summary>Field-to-field comparison (<c>compareTo</c>); only when the feature is enabled.</summary>
public sealed record FieldOperand(FieldRef Field) : QueryOperand;

/// <summary>Native JSON scalar (§12). Numbers keep their exact source text so no precision is lost before typed conversion.</summary>
public abstract record QueryValue
{
    public static readonly QueryValue Null = new NullValue();
}
public sealed record NullValue : QueryValue;
public sealed record StringValue(string Value) : QueryValue;
public sealed record BooleanValue(bool Value) : QueryValue;
public sealed record NumberValue(string Text) : QueryValue;
/// <summary>A CLR value supplied by trusted server code (row policies). The parser never produces this.</summary>
public sealed record ClrValue(object Value) : QueryValue;
