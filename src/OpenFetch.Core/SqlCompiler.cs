using System.Collections.Immutable;
using System.Text;

namespace OpenFetch;

/// <summary>
/// Shared SQL generation (§19) for relational providers. Identifiers come only from trusted metadata and are always quoted;
/// aliases are generated (t0..tn, c0..cn); every value is a parameter. Providers override the dialect hooks only.
/// </summary>
public abstract class SqlCompiler : IQueryCompiler
{
    public abstract string ProviderName { get; }
    public abstract string CompilerVersion { get; }
    protected abstract int MaximumParameters { get; }

    public abstract string Quote(string identifier);
    protected abstract string True { get; }
    protected abstract string False { get; }
    /// <summary>Characters (besides the escape character itself) that LIKE treats as wildcards in this dialect.</summary>
    protected abstract string LikeWildcards { get; }
    /// <summary>Renders <c>column [NOT] IN (values)</c> with a single parameter so SQL text does not depend on list length.</summary>
    protected abstract void In(SqlBuilder b, ResolvedField field, ImmutableArray<object> values, bool negate);
    /// <summary>Renders one ORDER BY term with NULLs last for ASC and first for DESC (identical across providers).</summary>
    protected abstract void OrderTerm(SqlBuilder b, ResolvedOrder order, string? distinctAlias);
    protected abstract void Paging(SqlBuilder b, int limit, int offset);
    public abstract CompiledCommand CompileSessionSetup(IReadOnlyList<KeyValuePair<string, string?>> settings);
    public virtual CompiledCommand CompileSessionTeardown(IReadOnlyList<KeyValuePair<string, string?>> settings) => new("", []);

    public QueryPlan Compile(ResolvedQuery q)
    {
        var columns = ResultColumn.For(q);
        var b = new SqlBuilder(this);
        var sql = b.Sql;

        sql.Append("SELECT ");
        if (q.Distinct) sql.Append("DISTINCT ");
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            b.Column(columns[i].Field);
            sql.Append(" AS c").Append(i);
        }

        sql.Append("\nFROM ");
        Table(sql, q.Dataset.Table);
        sql.Append(" AS t0");
        foreach (var j in q.Joins)
        {
            sql.Append("\nLEFT JOIN ");
            Table(sql, j.Target.Table);
            sql.Append(" AS ").Append(j.Alias).Append(" ON ");
            for (var i = 0; i < j.Relationship.Join.Length; i++)
            {
                if (i > 0) sql.Append(" AND ");
                var (source, target) = j.Relationship.Join[i];
                b.Column(j.ParentAlias, source);
                sql.Append(" = ");
                b.Column(j.Alias, target);
            }
            if (j.Policy is not null) { sql.Append(" AND ("); b.Filter(j.Policy); sql.Append(')'); }
        }

        // Policy first and parenthesized separately: the client filter can never negate or escape it (§16.4).
        var predicates = new List<Action>();
        if (q.Policy is not null) predicates.Add(() => b.Filter(q.Policy));
        if (q.Where is not null) predicates.Add(() => b.Filter(q.Where));
        if (q.After is { } after) predicates.Add(() => b.Keyset(q.OrderBy, after));
        for (var i = 0; i < predicates.Count; i++)
        {
            sql.Append(i == 0 ? "\nWHERE (" : "\n  AND (");
            predicates[i]();
            sql.Append(')');
        }

        sql.Append("\nORDER BY ");
        for (var i = 0; i < q.OrderBy.Length; i++)
        {
            if (i > 0) sql.Append(", ");
            var o = q.OrderBy[i];
            OrderTerm(b, o, q.Distinct ? "c" + columns.ToList().FindIndex(c => c.Role == ResultRole.Output && ResultColumn.Same(c.Field, o.Field)) : null);
        }
        Paging(b, q.PageSize + 1, q.Offset);

        if (b.Parameters.Count > MaximumParameters) throw QueryErrors.TooComplex("The query needs more parameters than the provider allows.");
        return new QueryPlan(sql.ToString(), [.. b.Parameters], columns);
    }

    private void Table(StringBuilder sql, PhysicalTable t)
    {
        if (t.Schema is not null) sql.Append(Quote(t.Schema)).Append('.');
        sql.Append(Quote(t.Name));
    }

    /// <summary>Escapes LIKE wildcards with backslash; use with <c>ESCAPE '\'</c>.</summary>
    public string EscapeLike(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (ch == '\\' || LikeWildcards.Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public sealed class SqlBuilder(SqlCompiler dialect)
    {
        public readonly StringBuilder Sql = new();
        public readonly List<QueryParameter> Parameters = [];

        public string Parameter(object? value)
        {
            var name = "p" + Parameters.Count;
            Parameters.Add(new QueryParameter(name, value));
            return "@" + name;
        }

        public void Column(ResolvedField f) => Column(f.Alias, f.Column);
        public void Column(string alias, ColumnDescriptor c) => Sql.Append(alias).Append('.').Append(dialect.Quote(c.Physical.Name));

        public static object? ToProvider(ColumnDescriptor c, object? value) =>
            value is null ? null : c.Physical.ToProvider is { } convert ? convert(value) : value;

        public string Value(ColumnDescriptor c, object? value) => Parameter(ToProvider(c, value));

        public void Filter(ResolvedFilter f)
        {
            switch (f)
            {
                case ResolvedLogical { Children.IsEmpty: true } l:
                    Sql.Append(l.Operator == LogicalOperator.And ? dialect.True : dialect.False);
                    break;
                case ResolvedLogical l:
                    for (var i = 0; i < l.Children.Length; i++)
                    {
                        if (i > 0) Sql.Append(l.Operator == LogicalOperator.And ? " AND " : " OR ");
                        Sql.Append('(');
                        Filter(l.Children[i]);
                        Sql.Append(')');
                    }
                    break;
                case ResolvedNot n:
                    Sql.Append("NOT (");
                    Filter(n.Operand);
                    Sql.Append(')');
                    break;
                case ResolvedComparison c:
                    Comparison(c);
                    break;
            }
        }

        private void Comparison(ResolvedComparison c)
        {
            var col = c.Field.Column;
            switch (c.Operator, c.Right)
            {
                case (QueryOperator.In or QueryOperator.NotIn, ResolvedList l):
                    dialect.In(this, c.Field, l.Values, c.Operator == QueryOperator.NotIn);
                    return;
            }
            Column(c.Field);
            switch (c.Operator, c.Right)
            {
                case (QueryOperator.Equal, ResolvedConstant { Value: null }): Sql.Append(" IS NULL"); break;
                case (QueryOperator.NotEqual, ResolvedConstant { Value: null }): Sql.Append(" IS NOT NULL"); break;
                case (_, ResolvedFieldOperand o): Sql.Append(' ').Append(Symbol(c.Operator)).Append(' '); Column(o.Field); break;
                case (QueryOperator.Between or QueryOperator.NotBetween, ResolvedList l):
                    Sql.Append(c.Operator == QueryOperator.Between ? " BETWEEN " : " NOT BETWEEN ")
                       .Append(Value(col, l.Values[0])).Append(" AND ").Append(Value(col, l.Values[1]));
                    break;
                case (QueryOperator.Contains or QueryOperator.StartsWith or QueryOperator.EndsWith, ResolvedConstant k):
                    var escaped = dialect.EscapeLike((string)k.Value!);
                    var pattern = c.Operator switch
                    {
                        QueryOperator.Contains => "%" + escaped + "%",
                        QueryOperator.StartsWith => escaped + "%",
                        _ => "%" + escaped,
                    };
                    Sql.Append(" LIKE ").Append(Parameter(pattern)).Append(@" ESCAPE '\'");
                    break;
                case (_, ResolvedConstant k): Sql.Append(' ').Append(Symbol(c.Operator)).Append(' ').Append(Value(col, k.Value)); break;
                default: throw QueryErrors.UnsupportedFeature($"Operator '{QueryOperators.Name(c.Operator)}' is not supported by this provider.");
            }
        }

        /// <summary>Keyset predicate in expanded OR form so mixed ASC/DESC orderings work (§20.1). Cursor fields are non-null.</summary>
        public void Keyset(ImmutableArray<ResolvedOrder> order, ImmutableArray<object?> values)
        {
            for (var i = 0; i < order.Length; i++)
            {
                Sql.Append(i == 0 ? "(" : " OR (");
                for (var e = 0; e < i; e++)
                {
                    Column(order[e].Field);
                    Sql.Append(" = ").Append(Value(order[e].Field.Column, values[e])).Append(" AND ");
                }
                Column(order[i].Field);
                Sql.Append(order[i].Direction == SortDirection.Ascending ? " > " : " < ").Append(Value(order[i].Field.Column, values[i])).Append(')');
            }
        }

        private static string Symbol(QueryOperator op) => op switch
        {
            QueryOperator.Equal => "=",
            QueryOperator.NotEqual => "<>",
            QueryOperator.GreaterThan => ">",
            QueryOperator.GreaterThanOrEqual => ">=",
            QueryOperator.LessThan => "<",
            QueryOperator.LessThanOrEqual => "<=",
            _ => throw QueryErrors.UnsupportedFeature($"Operator '{QueryOperators.Name(op)}' is not supported here."),
        };
    }
}
