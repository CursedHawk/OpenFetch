using System.Collections.Frozen;

namespace OpenFetch;

/// <summary>Core operators (§14). <c>like</c> is deliberately absent: its provider semantics are not documented in v0.1.</summary>
public enum QueryOperator
{
    Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual,
    In, NotIn, Between, NotBetween,
    Contains, StartsWith, EndsWith,
}

/// <summary>Logical scalar kinds that a column can have; the public type name is <see cref="QueryOperators.TypeName"/>.</summary>
public enum ScalarKind { String, Integer, Decimal, Number, Boolean, Guid, DateTime, Date, Time, Enum }

public static class QueryOperators
{
    private static readonly (QueryOperator Op, string Name)[] Table =
    [
        (QueryOperator.Equal, "eq"), (QueryOperator.NotEqual, "ne"),
        (QueryOperator.GreaterThan, "gt"), (QueryOperator.GreaterThanOrEqual, "gte"),
        (QueryOperator.LessThan, "lt"), (QueryOperator.LessThanOrEqual, "lte"),
        (QueryOperator.In, "in"), (QueryOperator.NotIn, "notIn"),
        (QueryOperator.Between, "between"), (QueryOperator.NotBetween, "notBetween"),
        (QueryOperator.Contains, "contains"), (QueryOperator.StartsWith, "startsWith"), (QueryOperator.EndsWith, "endsWith"),
    ];

    private static readonly FrozenDictionary<string, QueryOperator> ByName = Table.ToFrozenDictionary(t => t.Name, t => t.Op, StringComparer.Ordinal);

    public static string Name(QueryOperator op) => Table[(int)op].Name;

    public static bool TryParse(string name, out QueryOperator op) => ByName.TryGetValue(name, out op);

    public static readonly FrozenSet<QueryOperator> Comparison =
        [QueryOperator.Equal, QueryOperator.NotEqual, QueryOperator.GreaterThan, QueryOperator.GreaterThanOrEqual, QueryOperator.LessThan, QueryOperator.LessThanOrEqual];
    public static readonly FrozenSet<QueryOperator> Collections = [QueryOperator.In, QueryOperator.NotIn, QueryOperator.Between, QueryOperator.NotBetween];
    public static readonly FrozenSet<QueryOperator> Strings = [QueryOperator.Contains, QueryOperator.StartsWith, QueryOperator.EndsWith];

    private static readonly FrozenSet<QueryOperator> Equality = [QueryOperator.Equal, QueryOperator.NotEqual];
    private static readonly FrozenSet<QueryOperator> Set = [QueryOperator.Equal, QueryOperator.NotEqual, QueryOperator.In, QueryOperator.NotIn];
    private static readonly FrozenSet<QueryOperator> Ordered = [.. Comparison, .. Collections];
    private static readonly FrozenSet<QueryOperator> Text = [.. Set, .. Strings];

    /// <summary>Default operator set for a kind; also the upper bound of what may be allowed explicitly.</summary>
    public static FrozenSet<QueryOperator> DefaultsFor(ScalarKind kind) => kind switch
    {
        ScalarKind.Boolean => Equality,
        ScalarKind.Guid or ScalarKind.Enum => Set,
        ScalarKind.String => Text,
        _ => Ordered,
    };

    /// <summary>Every operator that is meaningful for a kind. Strings may additionally be range-compared (collation-dependent).</summary>
    public static FrozenSet<QueryOperator> ValidFor(ScalarKind kind) => kind == ScalarKind.String ? AllOperators : DefaultsFor(kind);

    private static readonly FrozenSet<QueryOperator> AllOperators = Enum.GetValues<QueryOperator>().ToFrozenSet();

    public static string TypeName(ScalarKind kind) => kind switch
    {
        ScalarKind.DateTime => "dateTime",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>Maps a CLR type (nullable unwrapped) to its scalar kind; null when unsupported.</summary>
    public static ScalarKind? KindOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum) return ScalarKind.Enum;
        return Type.GetTypeCode(type) switch
        {
            TypeCode.String => ScalarKind.String,
            TypeCode.Boolean => ScalarKind.Boolean,
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 => ScalarKind.Integer,
            TypeCode.Decimal => ScalarKind.Decimal,
            TypeCode.Single or TypeCode.Double => ScalarKind.Number,
            TypeCode.DateTime => ScalarKind.DateTime,
            _ when type == typeof(Guid) => ScalarKind.Guid,
            _ when type == typeof(DateTimeOffset) => ScalarKind.DateTime,
            _ when type == typeof(DateOnly) => ScalarKind.Date,
            _ when type == typeof(TimeOnly) => ScalarKind.Time,
            _ => null,
        };
    }
}
