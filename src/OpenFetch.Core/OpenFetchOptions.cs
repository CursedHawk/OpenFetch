using System.Linq.Expressions;
using System.Reflection;

namespace OpenFetch;

/// <summary>Fluent configuration (§7.6, §27). Conflicting registrations never broaden exposure: false/Hide/min/intersection win.</summary>
public sealed class OpenFetchOptions
{
    public OpenFetchOptions() => Security = new SecurityOptions(this);

    public QueryLimits Limits { get; set; } = new();
    public QueryFeatures Features { get; set; } = new();
    public SecurityOptions Security { get; }

    /// <summary>Explicit exposure convention: every public scalar property of an exposed dataset becomes a select/filter/sort column
    /// (camelCase name) unless hidden. Off by default (§7.1).</summary>
    public bool ExposeColumnsByConvention { get; set; }

    /// <summary>Protected diagnostic mode: SQL text (never parameter values) is logged at Debug level (§25).</summary>
    public bool DiagnosticMode { get; set; }

    /// <summary>Set by a provider package, e.g. <c>UsePostgreSql()</c>.</summary>
    public IQueryCompiler? Compiler { get; set; }

    internal Dictionary<Type, DatasetConfig> Datasets { get; } = [];

    public DatasetBuilder<T> Dataset<T>() where T : class
    {
        if (!Datasets.TryGetValue(typeof(T), out var config)) Datasets[typeof(T)] = config = new DatasetConfig();
        return new DatasetBuilder<T>(config);
    }
}

public sealed class SecurityOptions(OpenFetchOptions owner)
{
    public bool AuthenticatedUserRequired { get; private set; }
    public string? TenantClaimType { get; private set; }
    public bool DatabaseRowLevelSecurity { get; private set; }
    /// <summary>Transaction-local settings applied before each query (keys become <c>openfetch.&lt;key&gt;</c>).</summary>
    public Func<QuerySecurityContext, IEnumerable<KeyValuePair<string, string?>>>? DatabaseSessionValues { get; private set; }

    public SecurityOptions RequireAuthenticatedUser()
    {
        AuthenticatedUserRequired = true;
        return this;
    }

    /// <summary>Reads the tenant from this claim; a caller without it is rejected (403).</summary>
    public SecurityOptions UseTenantClaim(string claimType)
    {
        TenantClaimType = claimType;
        return this;
    }

    /// <summary>Sets trusted values on the query's own transaction for database RLS. Default: <c>openfetch.tenant_id</c> = tenant.</summary>
    public SecurityOptions UseDatabaseRowLevelSecurity(Func<QuerySecurityContext, IEnumerable<KeyValuePair<string, string?>>>? values = null)
    {
        DatabaseRowLevelSecurity = true;
        DatabaseSessionValues = values ?? (ctx => [new("tenant_id", ctx.TenantId)]);
        return this;
    }

    public SecurityOptions MaximumComplexity(int score)
    {
        owner.Limits = owner.Limits with { MaximumComplexity = score };
        return this;
    }
}

internal static class Deny
{
    /// <summary>A second, conflicting boolean registration can only narrow: false wins.</summary>
    public static void Set(ref bool? slot, bool value) => slot = slot is null ? value : slot.Value && value;

    public static void Min(ref int? slot, int value) => slot = slot is null ? value : Math.Min(slot.Value, value);

    public static void Name(ref string? slot, string value, List<string> conflicts, string what)
    {
        if (slot is not null && slot != value) conflicts.Add($"{what} registered with conflicting names '{slot}' and '{value}'");
        slot ??= value;
    }
}

internal sealed class DatasetConfig
{
    public string? Name;
    public bool Exposed, Hidden, Deprecated, QueryFilterAcknowledged;
    public string? Description, DeprecationMessage;
    public int? DefaultPageSize, MaximumPageSize, MaximumRelationshipDepth;
    public DatasetCapabilities Enabled, Disabled;
    public readonly List<string> Policies = [];
    public readonly List<LambdaExpression> RowPolicies = [];
    public readonly Dictionary<string, string[]> Projections = [];
    public readonly Dictionary<string, MemberConfig> Members = [];
    public readonly List<string> Conflicts = [];

    public MemberConfig Member(LambdaExpression selector)
    {
        if (selector.Body is not MemberExpression { Member: PropertyInfo p } m || m.Expression != selector.Parameters[0])
            throw new ArgumentException("Selector must be a direct property access such as x => x.Total.", nameof(selector));
        if (!Members.TryGetValue(p.Name, out var config)) Members[p.Name] = config = new MemberConfig();
        return config;
    }
}

internal sealed class MemberConfig
{
    public string? Name;
    public bool Exposed, Hidden, Deprecated;
    public string? Description, DeprecationMessage;
    public bool? Select, Filter, Sort, Group, Aggregate, Cursor, Expand;
    public HashSet<QueryOperator>? Operators;
    public int? MaximumDepth;
    public readonly List<string> Policies = [];
}

public sealed class DatasetBuilder<T> where T : class
{
    private readonly DatasetConfig _c;
    internal DatasetBuilder(DatasetConfig config) => _c = config;

    public DatasetBuilder<T> Expose(string name)
    {
        _c.Exposed = true;
        Deny.Name(ref _c.Name, name, _c.Conflicts, $"Dataset {typeof(T).Name}");
        return this;
    }

    /// <summary>Security-denying: wins over attributes and any other registration.</summary>
    public DatasetBuilder<T> Hide() { _c.Hidden = true; return this; }
    public DatasetBuilder<T> Description(string text) { _c.Description = text; return this; }
    public DatasetBuilder<T> RequirePolicy(string policy) { _c.Policies.Add(policy); return this; }
    public DatasetBuilder<T> Deprecate(string? message = null) { _c.Deprecated = true; _c.DeprecationMessage = message; return this; }

    public DatasetBuilder<T> PageSize(int defaultSize, int maximumSize)
    {
        Deny.Min(ref _c.DefaultPageSize, defaultSize);
        Deny.Min(ref _c.MaximumPageSize, maximumSize);
        return this;
    }

    public DatasetBuilder<T> MaximumRelationshipDepth(int depth) { Deny.Min(ref _c.MaximumRelationshipDepth, depth); return this; }
    public DatasetBuilder<T> Enable(DatasetCapabilities capabilities) { _c.Enabled |= capabilities; return this; }
    /// <summary>Disable wins over Enable regardless of order.</summary>
    public DatasetBuilder<T> Disable(DatasetCapabilities capabilities) { _c.Disabled |= capabilities; return this; }

    /// <summary>Confirms that the entity's EF global query filter is intentionally not applied (OpenFetch SQL bypasses it).</summary>
    public DatasetBuilder<T> AcknowledgeEfQueryFilterNotApplied() { _c.QueryFilterAcknowledged = true; return this; }

    /// <summary>Trusted predicate using only the entity, e.g. <c>o =&gt; !o.IsDeleted</c>.</summary>
    public DatasetBuilder<T> ApplyRowPolicy(Expression<Func<T, bool>> predicate) { _c.RowPolicies.Add(predicate); return this; }

    /// <summary>Trusted predicate using a per-request context resolved from DI, e.g. <c>(o, ctx) =&gt; o.TenantId == ctx.TenantId</c>.</summary>
    public DatasetBuilder<T> ApplyRowPolicy<TContext>(Expression<Func<T, TContext, bool>> predicate) where TContext : class
    {
        _c.RowPolicies.Add(predicate);
        return this;
    }

    /// <summary>Named fixed projection (§18.5): <c>POST {prefix}/{dataset}/{name}</c> always returns exactly these fields (dotted paths allowed).</summary>
    public DatasetBuilder<T> Projection(string name, params string[] fields)
    {
        if (_c.Projections.TryGetValue(name, out var existing) && !existing.SequenceEqual(fields))
            _c.Conflicts.Add($"Projection '{name}' on {typeof(T).Name} registered with different fields");
        _c.Projections[name] = fields;
        return this;
    }

    public ColumnBuilder Column<TProperty>(Expression<Func<T, TProperty>> property) => new(_c.Member(property), _c.Conflicts, typeof(T).Name);
    public RelationshipBuilder Relationship<TProperty>(Expression<Func<T, TProperty>> navigation) => new(_c.Member(navigation), _c.Conflicts, typeof(T).Name);
}

public sealed class ColumnBuilder
{
    private readonly MemberConfig _m;
    private readonly List<string> _conflicts;
    private readonly string _owner;
    internal ColumnBuilder(MemberConfig member, List<string> conflicts, string owner) => (_m, _conflicts, _owner) = (member, conflicts, owner);

    /// <summary>Opts the column in (select enabled unless denied). Name defaults to camelCase property name.</summary>
    public ColumnBuilder Expose(string? name = null)
    {
        _m.Exposed = true;
        if (name is not null) Deny.Name(ref _m.Name, name, _conflicts, $"Column on {_owner}");
        return this;
    }

    public ColumnBuilder Hide() { _m.Hidden = true; return this; }
    public ColumnBuilder Select(bool enabled = true) { Deny.Set(ref _m.Select, enabled); return this; }
    public ColumnBuilder Filter(bool enabled = true) { Deny.Set(ref _m.Filter, enabled); return this; }
    public ColumnBuilder Sort(bool enabled = true) { Deny.Set(ref _m.Sort, enabled); return this; }
    public ColumnBuilder Cursor(bool enabled = true) { Deny.Set(ref _m.Cursor, enabled); return this; }
    public ColumnBuilder Group(bool enabled = true) { Deny.Set(ref _m.Group, enabled); return this; }
    public ColumnBuilder Aggregate(bool enabled = true) { Deny.Set(ref _m.Aggregate, enabled); return this; }

    /// <summary>Repeated calls intersect.</summary>
    public ColumnBuilder Operators(params QueryOperator[] operators)
    {
        if (_m.Operators is null) _m.Operators = [.. operators];
        else _m.Operators.IntersectWith(operators);
        return this;
    }

    public ColumnBuilder RequirePolicy(string policy) { _m.Policies.Add(policy); return this; }
    public ColumnBuilder Description(string text) { _m.Description = text; return this; }
    public ColumnBuilder Deprecate(string? message = null) { _m.Deprecated = true; _m.DeprecationMessage = message; return this; }
}

public sealed class RelationshipBuilder
{
    private readonly MemberConfig _m;
    private readonly List<string> _conflicts;
    private readonly string _owner;
    internal RelationshipBuilder(MemberConfig member, List<string> conflicts, string owner) => (_m, _conflicts, _owner) = (member, conflicts, owner);

    public RelationshipBuilder Expose(string? name = null)
    {
        _m.Exposed = true;
        if (name is not null) Deny.Name(ref _m.Name, name, _conflicts, $"Relationship on {_owner}");
        return this;
    }

    public RelationshipBuilder Hide() { _m.Hidden = true; return this; }
    public RelationshipBuilder Expand(bool enabled = true) { Deny.Set(ref _m.Expand, enabled); return this; }
    public RelationshipBuilder Select(bool enabled = true) { Deny.Set(ref _m.Select, enabled); return this; }
    public RelationshipBuilder Filter(bool enabled = true) { Deny.Set(ref _m.Filter, enabled); return this; }
    public RelationshipBuilder Sort(bool enabled = true) { Deny.Set(ref _m.Sort, enabled); return this; }
    public RelationshipBuilder MaximumDepth(int depth) { Deny.Min(ref _m.MaximumDepth, depth); return this; }
    public RelationshipBuilder RequirePolicy(string policy) { _m.Policies.Add(policy); return this; }
    public RelationshipBuilder Description(string text) { _m.Description = text; return this; }
}
