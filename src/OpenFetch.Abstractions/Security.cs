using System.Collections.Concurrent;
using System.Security.Claims;

namespace OpenFetch;

/// <summary>Per-request trusted security context. Tenant identity comes only from authentication (§16.6), never from the query.</summary>
public sealed class QuerySecurityContext(
    ClaimsPrincipal user,
    string? tenantId,
    IServiceProvider services,
    Func<string, CancellationToken, ValueTask<bool>> authorize)
{
    private readonly ConcurrentDictionary<string, bool> _decisions = new(StringComparer.Ordinal);

    public ClaimsPrincipal User { get; } = user;
    public string? TenantId { get; } = tenantId;
    public IServiceProvider Services { get; } = services;

    /// <summary>Evaluates a named policy once per request and caches the decision.</summary>
    public async ValueTask<bool> IsAuthorizedAsync(string policy, CancellationToken cancellationToken)
    {
        if (_decisions.TryGetValue(policy, out var allowed)) return allowed;
        allowed = await authorize(policy, cancellationToken);
        _decisions[policy] = allowed;
        return allowed;
    }

    public async ValueTask<bool> IsAuthorizedAsync(IEnumerable<string> policies, CancellationToken cancellationToken)
    {
        foreach (var policy in policies)
            if (!await IsAuthorizedAsync(policy, cancellationToken)) return false;
        return true;
    }
}

/// <summary>Supplies additional trusted predicates (§16.4). Field names resolve against all mapped columns, hidden ones included.</summary>
public interface IRowPolicyProvider
{
    ValueTask<FilterNode?> BuildPolicyAsync(QuerySecurityContext context, DatasetDescriptor dataset, CancellationToken cancellationToken);
}
