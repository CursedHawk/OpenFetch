namespace OpenFetch;

/// <summary>Structural limits (§22). Defaults are the spec's recommended values; all are disclosed via capabilities.</summary>
public sealed record QueryLimits
{
    public int MaximumDocumentBytes { get; init; } = 64 * 1024;
    public int DefaultPageSize { get; init; } = 100;
    public int MaximumPageSize { get; init; } = 500;
    public int MaximumOffset { get; init; } = 10_000;
    public int MaximumRelationships { get; init; } = 3;
    public int MaximumRelationshipDepth { get; init; } = 2;
    public int MaximumFilterDepth { get; init; } = 6;
    public int MaximumConditions { get; init; } = 50;
    public int MaximumInValues { get; init; } = 200;
    public int MaximumSelectedFields { get; init; } = 50;
    public int MaximumComplexity { get; init; } = 150;
    public int MaximumResponseBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>Upper bound on rows fetched by collection expansions per request (page size × per-parent 'first', summed).</summary>
    public int MaximumExpandedRows { get; init; } = 5_000;
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CursorLifetime { get; init; } = TimeSpan.FromHours(1);
}
