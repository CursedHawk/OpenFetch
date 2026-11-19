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
    /// <summary>Row-count function returning a 64-bit integer.</summary>
    protected virtual string CountFunction => "COUNT";
    /// <summary>Exact decimal type used to normalize averages so all providers return identical values.</summary>
    protected abstract string AverageDecimalType { get; }
    /// <summary>ORDER BY used when nothing needs ordering (a single global aggregate row); null = omit the clause.</summary>
    protected virtual string? EmptyOrderBy => null;
    public abstract CompiledCommand CompileSessionSetup(IReadOnlyList<KeyValuePair<string, string?>> settings);
    public virtual CompiledCommand CompileSessionTeardown(IReadOnlyList<KeyValuePair<string, string?>> settings) => new("", []);

    public QueryPlan Compile(ResolvedQuery q)
    {
        if (!q.Secured) throw new InvalidOperationException("Refusing to compile a query that has not passed row-policy injection.");
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
        Source(b, q.Dataset, "t0", nested: false);
        foreach (var j in q.Joins)
        {
            sql.Append("\nLEFT JOIN ");
            Source(b, j.Target, j.Alias, nested: true);
            sql.Append(" ON ");
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
        // Aggregates still see only policy-filtered rows: grouping happens after WHERE (§21).
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

        if (!q.GroupBy.IsEmpty)
        {
            sql.Append("\nGROUP BY ");
            for (var i = 0; i < q.GroupBy.Length; i++)
            {
                if (i > 0) sql.Append(", ");
                b.Column(q.GroupBy[i]);
            }
        }
        if (q.Having is not null) { sql.Append("\nHAVING ("); b.Filter(q.Having); sql.Append(')'); }

        if (q.OrderBy.IsEmpty) { if (EmptyOrderBy is { } empty) sql.Append("\nORDER BY ").Append(empty); }
        else sql.Append("\nORDER BY ");
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

    /// <summary>
    /// Split query for one collection expansion: per-parent top-N via ROW_NUMBER over the foreign key, restricted to the
    /// given parent keys (one parameter, shape-stable), with the target's trusted policy and the client filter.
    /// Result columns: c0 = foreign key, then the expansion's select in order.
    /// </summary>
    public CompiledCommand CompileExpansion(ResolvedExpansion e, IReadOnlyList<object> parentKeys)
    {
        if (!e.Secured) throw new InvalidOperationException("Refusing to compile an expansion that has not passed row-policy injection.");
        var b = new SqlBuilder(this);
        var sql = b.Sql;
        var alias = e.OrderBy[0].Field.Alias;
        var fk = new ResolvedField(alias, "", e.ForeignKey);
        sql.Append("SELECT ");
        for (var i = 0; i <= e.Select.Length; i++) sql.Append(i == 0 ? "" : ", ").Append("x.c").Append(i);
        sql.Append("\nFROM (SELECT ");
        b.Column(fk);
        sql.Append(" AS c0");
        for (var i = 0; i < e.Select.Length; i++) { sql.Append(", "); b.Column(e.Select[i]); sql.Append(" AS c").Append(i + 1); }
        sql.Append(", ROW_NUMBER() OVER (PARTITION BY ");
        b.Column(fk);
        sql.Append(" ORDER BY ");
        for (var i = 0; i < e.OrderBy.Length; i++) { if (i > 0) sql.Append(", "); OrderTerm(b, e.OrderBy[i], null); }
        sql.Append(") AS rn\n  FROM ");
        Source(b, e.Target, alias, nested: false);
        sql.Append("\n  WHERE (");
        In(b, fk, [.. parentKeys], negate: false);
        sql.Append(')');
        if (e.Policy is not null) { sql.Append(" AND ("); b.Filter(e.Policy); sql.Append(')'); }
        if (e.Where is not null) { sql.Append(" AND ("); b.Filter(e.Where); sql.Append(')'); }
        sql.Append(") x\nWHERE x.rn <= ").Append(b.Parameter(e.Limit)).Append("\nORDER BY x.c0, x.rn");
        if (b.Parameters.Count > MaximumParameters) throw QueryErrors.TooComplex("The query needs more parameters than the provider allows.");
        return new CompiledCommand(sql.ToString(), [.. b.Parameters]);
    }

    /// <summary>
    /// The physical row source of a dataset under <paramref name="alias"/>: a table, a table with TPT base tables joined 1:1
    /// (aliases alias_1..n, parenthesized when nested in a join), or a TPC UNION ALL whose columns are named after the dataset columns.
    /// </summary>
    internal void Source(SqlBuilder b, DatasetDescriptor d, string alias, bool nested)
    {
        var sql = b.Sql;
        if (!d.Union.IsEmpty)
        {
            var columns = d.AllColumns.Select(c => c.Physical.Name).Distinct().ToList();
            sql.Append('(');
            for (var i = 0; i < d.Union.Length; i++)
            {
                if (i > 0) sql.Append(" UNION ALL ");
                sql.Append("SELECT ").Append(string.Join(", ", columns.Select(c => Quote(d.Union[i].Columns[c]) + " AS " + Quote(c)))).Append(" FROM ");
                Table(sql, d.Union[i].Table);
            }
            sql.Append(") AS ").Append(alias);
            return;
        }
        var paren = nested && !d.InheritedTables.IsEmpty;
        if (paren) sql.Append('(');
        Table(sql, d.Table);
        sql.Append(" AS ").Append(alias);
        for (var i = 0; i < d.InheritedTables.Length; i++)
        {
            var t = d.InheritedTables[i];
            var joined = alias + "_" + (i + 1);
            sql.Append(" INNER JOIN ");
            Table(sql, t.Table);
            sql.Append(" AS ").Append(joined).Append(" ON ")
               .Append(string.Join(" AND ", t.Key.Select(k => $"{alias}.{Quote(k.Main)} = {joined}.{Quote(k.Joined)}")));
        }
        if (paren) sql.Append(')');
    }

    internal void Table(StringBuilder sql, PhysicalTable t)
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

        public void Column(ResolvedField f)
        {
            if (f.Aggregate is { } a) Aggregate(a);
            else Column(f.Alias, f.Column);
        }

        /// <summary>Aggregate with normalized result types: 64-bit counts and integer sums, decimal(38,10) averages.</summary>
        private void Aggregate(ResolvedAggregate a)
        {
            var arg = a.Argument;
            switch (a.Function)
            {
                case AggregateFunction.Count when arg is null: Sql.Append(dialect.CountFunction).Append("(*)"); break;
                case AggregateFunction.Count: Sql.Append(dialect.CountFunction).Append('('); Column(arg); Sql.Append(')'); break;
                case AggregateFunction.CountDistinct: Sql.Append(dialect.CountFunction).Append("(DISTINCT "); Column(arg!); Sql.Append(')'); break;
                case AggregateFunction.Sum when arg!.Column.Kind == ScalarKind.Integer:
                    Sql.Append("CAST(SUM(CAST("); Column(arg); Sql.Append(" AS bigint)) AS bigint)"); break;
                case AggregateFunction.Avg when arg!.Column.Kind is ScalarKind.Integer or ScalarKind.Decimal:
                    Sql.Append("CAST(AVG(CAST("); Column(arg); Sql.Append(" AS ").Append(dialect.AverageDecimalType).Append(")) AS ")
                       .Append(dialect.AverageDecimalType).Append(')');
                    break;
                default:
                    Sql.Append(a.Function switch { AggregateFunction.Sum => "SUM", AggregateFunction.Avg => "AVG", AggregateFunction.Min => "MIN", _ => "MAX" }).Append('(');
                    Column(arg!);
                    Sql.Append(')');
                    break;
            }
        }
        /// <summary>TPT columns that live in a base table use that table's alias (alias_n).</summary>
        public void Column(string alias, ColumnDescriptor c)
        {
            Sql.Append(alias);
            if (c.Physical.Table > 0) Sql.Append('_').Append(c.Physical.Table);
            Sql.Append('.').Append(dialect.Quote(c.Physical.Name));
        }

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
                case ResolvedExists x:
                    // The target's trusted policy lives inside the subquery: hidden related rows never satisfy 'any'.
                    if (!x.Secured) throw new InvalidOperationException("Refusing to compile an EXISTS that has not passed row-policy injection.");
                    Sql.Append("EXISTS (SELECT 1 FROM ");
                    dialect.Source(this, x.Target, x.Alias, nested: false);
                    Sql.Append(" WHERE ");
                    var (source, target) = x.Relationship.Join[0];
                    Column(x.Alias, target);
                    Sql.Append(" = ");
                    Column(x.ParentAlias, source);
                    if (x.Policy is not null) { Sql.Append(" AND ("); Filter(x.Policy); Sql.Append(')'); }
                    if (x.Where is not null) { Sql.Append(" AND ("); Filter(x.Where); Sql.Append(')'); }
                    Sql.Append(')');
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
