using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OpenFetch.AspNetCore;
using OpenFetch.Compiler.PostgreSql;

namespace OpenFetch.Tests;

/// <summary>Offline engine over the sample model with a fake caller; no HTTP involved.</summary>
public static class TestEngine
{
    public static readonly ICursorProtector Cursors = new DataProtectionCursorProtector(new EphemeralDataProtectionProvider());

    public static QueryEngine Create(QueryModel? model = null, TimeProvider? time = null, SecurityOptions? security = null, IEnumerable<IRowPolicyProvider>? providers = null) =>
        new(model ?? TestModel.Default, new PostgreSqlCompiler(), Cursors, security ?? new OpenFetchOptions().Security, providers, time);

    public static readonly string[] AllPolicies = ["Orders.FinancialData", "Orders.Read", "Customers.Read"];

    public static QuerySecurityContext Context(Guid tenant, string[]? policies = null, string? user = null)
    {
        policies ??= AllPolicies;
        var services = new ServiceCollection().AddSingleton(new TenantContext { TenantId = tenant }).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user ?? "user-" + tenant)], "test"));
        return new QuerySecurityContext(principal, tenant.ToString(), services, (p, _) => ValueTask.FromResult(policies.Contains(p)));
    }

    public static ValueTask<PreparedQuery> PrepareAsync(this QueryEngine engine, string dataset, string json, QuerySecurityContext context) =>
        engine.PrepareAsync(dataset, System.Text.Encoding.UTF8.GetBytes(json), context, TestContext.Current.CancellationToken);
}
