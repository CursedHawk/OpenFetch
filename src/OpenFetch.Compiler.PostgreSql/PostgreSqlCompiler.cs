using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenFetch.Compiler.PostgreSql;

/// <summary>
/// PostgreSQL compiler (§19). Identifiers come only from trusted metadata and are always quoted; aliases are generated (t0..tn, c0..cn);
/// every value is a parameter. IN uses <c>= ANY(@p)</c> with one array parameter so plans do not vary with list length.
/// String operators use LIKE with escaped wildcards and are case-sensitive under the column's collation.
/// </summary>
public sealed partial class PostgreSqlCompiler : IQueryCompiler
{
    public const int MaximumParameters = 65_535;

    public string ProviderName => "postgresql";
    public string CompilerVersion => "1";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SettingKey();

    public QueryPlan Compile(ResolvedQuery q)
    {
        var columns = ResultColumn.For(q);
        var b = new Builder();
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
            if (q.Distinct) sql.Append('c').Append(columns.ToList().FindIndex(c => c.Role == ResultRole.Output && ResultColumn.Same(c.Field, o.Field)));
            else b.Column(o.Field);
            sql.Append(o.Direction == SortDirection.Ascending ? " ASC NULLS LAST" : " DESC NULLS FIRST");
        }

        sql.Append("\nLIMIT ").Append(b.Parameter(q.PageSize + 1));
        if (q.Offset > 0) sql.Append(" OFFSET ").Append(b.Parameter(q.Offset));

        if (b.Parameters.Count > MaximumParameters) throw QueryErrors.TooComplex("The query needs more parameters than the provider allows.");
        return new QueryPlan(sql.ToString(), [.. b.Parameters], columns);
    }

    public CompiledCommand CompileSessionSetup(IReadOnlyList<KeyValuePair<string, string?>> settings)
    {
        var b = new Builder();
        b.Sql.Append("SET TRANSACTION READ ONLY");
        if (settings.Count > 0)
        {
            b.Sql.Append(";\nSELECT ");
            for (var i = 0; i < settings.Count; i++)
            {
                var (key, value) = settings[i];
                if (!SettingKey().IsMatch(key)) throw new InvalidOperationException($"Invalid RLS setting key '{key}'.");
                if (i > 0) b.Sql.Append(", ");
                // Transaction-local (is_local = true): reset at commit/rollback, never leaks through the connection pool.
                b.Sql.Append("set_config(").Append(b.Parameter("openfetch." + key)).Append(", ").Append(b.Parameter(value ?? "")).Append(", true)");
            }
        }
        return new CompiledCommand(b.Sql.ToString(), [.. b.Parameters]);
    }

    private static void Table(StringBuilder sql, PhysicalTable t)
    {
        if (t.Schema is not null) sql.Append(Quote(t.Schema)).Append('.');
        sql.Append(Quote(t.Name));
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private sealed class Builder
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
        public void Column(string alias, ColumnDescriptor c) => Sql.Append(alias).Append('.').Append(Quote(c.Physical.Name));

        private string Value(ColumnDescriptor c, object? value) =>
            Parameter(value is null ? null : c.Physical.ToProvider is { } convert ? convert(value) : value);

        private string Array(ColumnDescriptor c, ImmutableArray<object> values)
        {
            var array = System.Array.CreateInstance(c.Physical.ProviderClrType, values.Length);
            for (var i = 0; i < values.Length; i++) array.SetValue(c.Physical.ToProvider is { } convert ? convert(values[i]) : values[i], i);
            return Parameter(array);
        }

        public void Filter(ResolvedFilter f)
        {
            switch (f)
            {
                case ResolvedLogical { Children.IsEmpty: true } l:
                    Sql.Append(l.Operator == LogicalOperator.And ? "TRUE" : "FALSE");
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
            Column(c.Field);
            switch (c.Operator, c.Right)
            {
                case (QueryOperator.Equal, ResolvedConstant { Value: null }): Sql.Append(" IS NULL"); break;
                case (QueryOperator.NotEqual, ResolvedConstant { Value: null }): Sql.Append(" IS NOT NULL"); break;
                case (_, ResolvedFieldOperand o): Sql.Append(' ').Append(Symbol(c.Operator)).Append(' '); Column(o.Field); break;
                case (QueryOperator.In, ResolvedList l): Sql.Append(" = ANY(").Append(Array(col, l.Values)).Append(')'); break;
                case (QueryOperator.NotIn, ResolvedList l): Sql.Append(" <> ALL(").Append(Array(col, l.Values)).Append(')'); break;
                case (QueryOperator.Between or QueryOperator.NotBetween, ResolvedList l):
                    Sql.Append(c.Operator == QueryOperator.Between ? " BETWEEN " : " NOT BETWEEN ")
                       .Append(Value(col, l.Values[0])).Append(" AND ").Append(Value(col, l.Values[1]));
                    break;
                case (QueryOperator.Contains or QueryOperator.StartsWith or QueryOperator.EndsWith, ResolvedConstant k):
                    var escaped = ((string)k.Value!).Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
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

public static class PostgreSqlOptionsExtensions
{
    public static OpenFetchOptions UsePostgreSql(this OpenFetchOptions options)
    {
        options.Compiler = new PostgreSqlCompiler();
        return options;
    }
}
