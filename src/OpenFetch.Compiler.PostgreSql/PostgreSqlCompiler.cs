using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace OpenFetch.Compiler.PostgreSql;

/// <summary>
/// PostgreSQL dialect. IN uses <c>= ANY(@array)</c> (one typed array parameter); LIKE is case-sensitive under the column collation;
/// session setup is <c>SET TRANSACTION READ ONLY</c> plus transaction-local <c>set_config</c> for RLS.
/// </summary>
public sealed partial class PostgreSqlCompiler : SqlCompiler
{
    public override string ProviderName => "postgresql";
    public override string CompilerVersion => "1";
    protected override int MaximumParameters => 65_535;
    protected override string True => "TRUE";
    protected override string False => "FALSE";
    protected override string LikeWildcards => "%_";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SettingKey();

    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    protected override void In(SqlBuilder b, ResolvedField field, ImmutableArray<object> values, bool negate)
    {
        var c = field.Column;
        var array = Array.CreateInstance(c.Physical.ProviderClrType, values.Length);
        for (var i = 0; i < values.Length; i++) array.SetValue(SqlBuilder.ToProvider(c, values[i]), i);
        b.Column(field);
        b.Sql.Append(negate ? " <> ALL(" : " = ANY(").Append(b.Parameter(array)).Append(')');
    }

    protected override void OrderTerm(SqlBuilder b, ResolvedOrder o, string? distinctAlias)
    {
        if (distinctAlias is not null) b.Sql.Append(distinctAlias);
        else b.Column(o.Field);
        b.Sql.Append(o.Direction == SortDirection.Ascending ? " ASC NULLS LAST" : " DESC NULLS FIRST");
    }

    protected override void Paging(SqlBuilder b, int limit, int offset)
    {
        b.Sql.Append("\nLIMIT ").Append(b.Parameter(limit));
        if (offset > 0) b.Sql.Append(" OFFSET ").Append(b.Parameter(offset));
    }

    public override CompiledCommand CompileSessionSetup(IReadOnlyList<KeyValuePair<string, string?>> settings)
    {
        var b = new SqlBuilder(this);
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
}

public static class PostgreSqlOptionsExtensions
{
    public static OpenFetchOptions UsePostgreSql(this OpenFetchOptions options)
    {
        options.Compiler = new PostgreSqlCompiler();
        return options;
    }
}
