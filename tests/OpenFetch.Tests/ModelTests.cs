using OpenFetch.Tests.Domain;
using System.Collections.Immutable;

namespace OpenFetch.Tests;

public class ModelTests
{
    [Fact]
    public void Sample_model_exposes_only_opted_in_members()
    {
        var orders = TestModel.Default.Datasets["orders"];
        Assert.Equal(["createdAt", "id", "number", "status", "total"], orders.Columns.Keys.Order());
        Assert.DoesNotContain(orders.Columns.Values, c => c.PropertyName is "TenantId" or "IsDeleted" or "CustomerId");
        Assert.Contains(orders.AllColumns, c => c.PropertyName == "TenantId" && !c.Exposed);
        Assert.Equal(["customer"], orders.Relationships.Keys);
        Assert.Equal("customers", orders.Relationships["customer"].TargetDataset);
        Assert.Equal(Cardinality.Many, TestModel.Default.Datasets["customers"].Relationships["orders"].Cardinality);
        Assert.Equal(["Id"], orders.Key.Select(k => k.PropertyName));
    }

    [Fact]
    public void Physical_metadata_and_enum_conversion_are_captured()
    {
        var orders = TestModel.Default.Datasets["orders"];
        Assert.Equal(new PhysicalTable("sales", "Orders"), orders.Table);
        var status = orders.Columns["status"];
        Assert.Equal(["open", "processing", "shipped", "cancelled"], status.EnumValues);
        Assert.Equal(typeof(int), status.Physical.ProviderClrType);
        Assert.Equal(1, status.Physical.ToProvider!(OrderStatus.Processing));
        var total = orders.Columns["total"];
        Assert.Equal((18, 2), (total.Precision, total.Scale));
        Assert.Equal(["Orders.FinancialData"], total.Policies);
        Assert.Equal(32, orders.Columns["number"].MaxLength);
        Assert.True(orders.FindProperty("TenantId")!.Indexed);
        Assert.Equal([QueryOperator.Equal, QueryOperator.NotEqual, QueryOperator.In], orders.Columns["status"].Operators.Order());
    }

    [Fact]
    public void Fluent_overrides_attributes_and_conflicts_never_broaden()
    {
        var model = TestModel.Build(o =>
        {
            o.Dataset<Order>().Column(x => x.Number).Filter(true);
            o.Dataset<Order>().Column(x => x.Number).Filter(false); // conflicting registration: deny wins
            o.Dataset<Order>().Column(x => x.CreatedAt).Filter(false);
            o.Dataset<Customer>().Column(x => x.Country).Cursor(false).Group(true); // explicit fluent beats attribute default
            o.Dataset<Order>().Column(x => x.Total).RequirePolicy("Extra");
            o.Dataset<Order>().PageSize(50, 400).PageSize(80, 200);
        });
        var orders = model.Datasets["orders"];
        Assert.False(orders.Columns["number"].Can(ColumnCapabilities.Filter));
        Assert.False(orders.Columns["createdAt"].Can(ColumnCapabilities.Filter));
        Assert.True(model.Datasets["customers"].Columns["country"].Can(ColumnCapabilities.Group));
        Assert.Equal(["Extra", "Orders.FinancialData"], orders.Columns["total"].Policies); // policies accumulate
        Assert.Equal((50, 200), (orders.DefaultPageSize, orders.MaximumPageSize));
    }

    [Fact]
    public void Hide_wins_over_attribute_and_explicit_exposure()
    {
        var model = TestModel.Build(o => o.Dataset<Order>().Column(x => x.Total).Expose().Select(true).Hide());
        Assert.False(model.Datasets["orders"].Columns.ContainsKey("total"));

        var hiddenAttr = TestModel.Build(o => o.Dataset<Order>().Column(x => x.TenantId).Expose("tenant"));
        Assert.False(hiddenAttr.Datasets["orders"].Columns.ContainsKey("tenant")); // [QueryHidden] is absolute

        var noDataset = TestModel.Build(o => { o.Dataset<Order>().Hide(); o.Dataset<Customer>().Relationship(x => x.Orders).Hide(); });
        Assert.False(noDataset.Datasets.ContainsKey("orders"));
    }

    [Fact]
    public void Fluent_exposure_without_attributes_and_convention()
    {
        var model = TestModel.Build(o =>
        {
            o.Dataset<Order>().Column(x => x.IsDeleted).Expose();
            o.ExposeColumnsByConvention = true;
        });
        var orders = model.Datasets["orders"];
        Assert.True(orders.Columns["isDeleted"].Can(ColumnCapabilities.Select));
        Assert.True(orders.Columns["customerId"].Can(ColumnCapabilities.Filter)); // convention
        Assert.False(orders.Columns.ContainsKey("tenantId")); // still hidden
    }

    [Fact]
    public void Hiding_a_relationship_target_fails_startup()
    {
        var ex = Assert.Throws<OpenFetchConfigurationException>(() => TestModel.Build(o => o.Dataset<Customer>().Hide()));
        Assert.Contains(ex.Errors, e => e.Contains("relationship 'customer'") && e.Contains("not an exposed dataset"));
    }

    [Fact]
    public void Startup_validation_collects_all_errors()
    {
        var ex = Assert.Throws<OpenFetchConfigurationException>(() => TestModel.Build(o =>
        {
            o.Dataset<Customer>().Column(x => x.Country).Cursor(true);           // nullable cursor
            o.Dataset<Order>().Column(x => x.Status).Operators(QueryOperator.Contains); // invalid for enum
            o.Dataset<Order>().Column(x => x.Number).Expose("bad name").Expose("other"); // conflicting + invalid name
            o.Dataset<Customer>().Expose("orders");                               // duplicate dataset name
            o.Limits = o.Limits with { MaximumInValues = 0 };
        }));
        string[] expected = ["invalid public name", "cursor columns must be non-nullable", "'contains' is not valid", "conflicting names", "Duplicate dataset name",
            "limits must be positive"];
        foreach (var e in expected) Assert.Contains(ex.Errors, x => x.Contains(e));
    }

    [Fact]
    public void Model_hash_is_order_independent_and_tracks_capabilities()
    {
        var a = TestModel.Build(o => { o.Dataset<Order>().Column(x => x.Number).Filter(false); o.Dataset<Customer>().Description("x"); });
        var b = TestModel.Build(o => { o.Dataset<Customer>().Description("x"); o.Dataset<Order>().Column(x => x.Number).Filter(false); });
        var c = TestModel.Build(o => o.Dataset<Customer>().Description("x"));
        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Version, c.Version);
        Assert.NotEqual(c.Version, TestModel.Build(o => { o.Dataset<Customer>().Description("x"); o.Limits = o.Limits with { MaximumConditions = 10 }; }).Version);
    }

    [Fact]
    public void Row_policy_expression_translates_to_trusted_filter()
    {
        var policy = Assert.Single(TestModel.Default.Datasets["orders"].RowPolicies);
        Assert.Equal(typeof(TenantContext), policy.ContextType);
        var node = policy.Build(new TenantContext { TenantId = SeedData.TenantA });

        var and = Assert.IsType<LogicalFilter>(node);
        var tenant = Assert.IsType<ComparisonFilter>(and.Children[0]);
        Assert.Equal(("TenantId", QueryOperator.Equal), (tenant.Left.Path, tenant.Operator));
        Assert.Equal(new ClrValue(SeedData.TenantA), ((ConstantOperand)tenant.Right).Value);
        var notDeleted = Assert.IsType<NotFilter>(and.Children[1]);
        Assert.Equal("IsDeleted", Assert.IsType<ComparisonFilter>(notDeleted.Operand).Left.Path);
    }

    [Fact]
    public void Row_policy_supports_contains_and_flipped_comparisons()
    {
        var model = TestModel.Build(o => o.Dataset<Order>()
            .ApplyRowPolicy<AllowedStatuses>((x, c) => c.Statuses.Contains(x.Status) && 100m < x.Total));
        var node = (LogicalFilter)model.Datasets["orders"].RowPolicies[0].Build(new AllowedStatuses { Statuses = [OrderStatus.Open] });
        Assert.Equal(QueryOperator.In, ((ComparisonFilter)node.Children[0]).Operator);
        Assert.Equal(QueryOperator.GreaterThan, ((ComparisonFilter)node.Children[1]).Operator);

        var empty = model.Datasets["orders"].RowPolicies[0].Build(new AllowedStatuses { Statuses = [] });
        Assert.Empty(((LogicalFilter)((LogicalFilter)empty).Children[0]).Children); // empty OR = deny all
    }

    [Fact]
    public void Untranslatable_row_policy_fails_startup()
    {
        var ex = Assert.Throws<OpenFetchConfigurationException>(() =>
            TestModel.Build(o => o.Dataset<Order>().ApplyRowPolicy(x => x.Number.Length > 3)));
        Assert.Contains(ex.Errors, e => e.Contains("untranslatable"));
    }

    [Fact]
    public async Task Registered_extensions_pass_through_and_others_are_rejected()
    {
        var model = TestModel.Build(o => { TestModel.Configure(o); o.Features = o.Features with { Extensions = ["x-acme-trace"] }; });
        var engine = TestEngine.Create(model);
        var q = await engine.PrepareAsync("orders", """{"select":["id"],"x-acme-trace":{"id":"t-1"}}""", TestEngine.Context(SeedData.TenantA));
        Assert.Equal("""{"id":"t-1"}""", q.Extensions["x-acme-trace"]);
        var ex = await Assert.ThrowsAsync<OpenFetchException>(async () => await engine.PrepareAsync("orders", """{"select":["id"],"x-other-thing":1}""", TestEngine.Context(SeedData.TenantA)));
        Assert.Equal(("invalid_query", "/x-other-thing"), (ex.Code, ex.Path));
        Assert.NotEqual(TestModel.Default.Version, model.Version);
        Assert.Throws<OpenFetchConfigurationException>(() => TestModel.Build(o => o.Features = o.Features with { Extensions = ["trace"] }));
    }

    public sealed class AllowedStatuses
    {
        public OrderStatus[] Statuses { get; set; } = [];
    }
}
