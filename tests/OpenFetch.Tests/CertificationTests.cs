using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using OpenFetch.Certification;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>The reference server (sample host) must pass the black-box certification suite on every provider.</summary>
public static class Certify
{
    public static async Task<CertificationReport> RunAsync(WebApplication app)
    {
        var target = new HttpCertificationTarget(app.GetTestClient(), new Dictionary<Caller, Action<HttpRequestHeaders>>
        {
            [Caller.TenantA] = h => { h.Add("X-User", "alice"); h.Add("X-Tenant", SeedData.TenantA.ToString()); h.Add("X-Roles", "finance"); },
            [Caller.TenantB] = h => { h.Add("X-User", "bob"); h.Add("X-Tenant", SeedData.TenantB.ToString()); },
        });
        return await CertificationSuite.RunAsync(target, TestContext.Current.CancellationToken);
    }
}

[Collection("postgres")]
public class PostgresCertificationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Reference_server_is_certified()
    {
        await using var app = await TestHost.StartAsync(pg.AppConnectionString());
        var report = await Certify.RunAsync(app);
        Assert.True(report.Passed, report.ToString());
        Assert.True(report.Results.Count(r => r.Category == "security") >= 13);
    }

    [Fact]
    public async Task A_leaky_server_fails_certification()
    {
        // Without the tenant row policy the database role still enforces RLS, so use the owner connection to prove detection.
        await using var app = await TestHost.StartAsync(pg.ConnectionString, o => { }, b => { }, configureModel: false);
        var report = await Certify.RunAsync(app);
        Assert.False(report.Passed);
        Assert.Contains(report.Results, r => r is { Category: "security", Name: "isolation-select", Passed: false });
    }
}

[Collection("sqlserver")]
public class SqlServerCertificationTests(SqlServerFixture ms)
{
    [Fact]
    public async Task Reference_server_is_certified()
    {
        await using var app = await TestHost.StartAsync(ms.AppConnectionString(), sqlServer: true);
        var report = await Certify.RunAsync(app);
        Assert.True(report.Passed, report.ToString());
    }
}
