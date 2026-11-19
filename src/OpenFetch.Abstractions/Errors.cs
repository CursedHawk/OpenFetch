namespace OpenFetch;

/// <summary>A controlled query failure, rendered as Problem Details (§24). Never carries provider error text or physical identifiers.</summary>
public sealed class OpenFetchException(string code, int status, string title, string? detail = null, string? path = null) : Exception(detail ?? title)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    /// <summary>JSON Pointer into the request document.</summary>
    public string? Path { get; } = path;
    public Dictionary<string, object?> Extensions { get; } = [];

    public OpenFetchException With(string key, object? value)
    {
        Extensions[key] = value;
        return this;
    }
}

/// <summary>Stable error codes (§24) with their HTTP statuses.</summary>
public static class QueryErrors
{
    public static OpenFetchException InvalidJson(string detail, string? path = null) => new("invalid_json", 400, "Invalid JSON", detail, path);
    public static OpenFetchException InvalidQuery(string detail, string? path) => new("invalid_query", 400, "Invalid query document", detail, path);
    public static OpenFetchException DocumentTooLarge(int max) => new("document_too_large", 413, "Query document too large", $"The query document exceeds {max} bytes.");
    public static OpenFetchException UnsupportedVersion(string version, string path) => new("unsupported_version", 400, "Unsupported language version", $"Version '{version}' is not supported.", path);
    public static OpenFetchException UnknownDataset(string name) => new OpenFetchException("unknown_dataset", 404, "Unknown dataset", $"Dataset '{name}' does not exist.").With("dataset", name);
    public static OpenFetchException UnknownField(string dataset, string field, string path) =>
        new OpenFetchException("unknown_field", 422, "Unknown field", $"Field '{field}' does not exist or is not available for this use.", path).With("dataset", dataset).With("field", field);
    public static OpenFetchException InvalidRelationship(string dataset, string name, string path, string? detail = null) =>
        new OpenFetchException("invalid_relationship", 422, "Invalid relationship", detail ?? $"Relationship '{name}' does not exist or cannot be used here.", path).With("dataset", dataset);
    public static OpenFetchException UnsupportedOperator(string dataset, string field, QueryOperator op, IEnumerable<QueryOperator> allowed, string path) =>
        new OpenFetchException("unsupported_operator", 422, "Invalid query operator", $"Operator '{QueryOperators.Name(op)}' cannot be used with field '{field}'.", path)
            .With("dataset", dataset).With("field", field).With("allowedOperators", allowed.Order().Select(QueryOperators.Name).ToArray());
    public static OpenFetchException InvalidValueType(string detail, string path) => new("invalid_value_type", 422, "Invalid value", detail, path);
    public static OpenFetchException TooComplex(string detail, string? path = null) => new("query_too_complex", 422, "Query too complex", detail, path);
    public static OpenFetchException PageSizeExceeded(int max, string path) => new OpenFetchException("page_size_exceeded", 422, "Page size exceeded", $"The page size must be between 1 and {max}.", path).With("maxPageSize", max);
    public static OpenFetchException InvalidCursor(string path = "/page/after") => new("invalid_cursor", 422, "Invalid cursor", "The cursor is malformed, expired or was not issued by this server.", path);
    public static OpenFetchException CursorScopeMismatch(string path = "/page/after") => new("cursor_scope_mismatch", 422, "Cursor scope mismatch", "The cursor was issued for a different query, model version or caller.", path);
    public static OpenFetchException UnsupportedFeature(string detail, string? path = null) => new("unsupported_provider_feature", 422, "Unsupported feature", detail, path);
    public static OpenFetchException Timeout() => new("query_timeout", 504, "Query timeout", "The query exceeded the configured command timeout.");
    public static OpenFetchException ResponseTooLarge(int max) => new("response_limit_exceeded", 422, "Response limit exceeded", $"The response would exceed {max} bytes; request fewer rows or fields.");
    public static OpenFetchException Unauthenticated() => new("unauthenticated", 401, "Authentication required");
    public static OpenFetchException Forbidden(string detail) => new("forbidden", 403, "Forbidden", detail);
    public static OpenFetchException ExecutionFailed() => new("execution_failed", 500, "Query execution failed");
}

/// <summary>Startup failure: the model is invalid or ambiguous (§26). Lists every problem found, not only the first.</summary>
public sealed class OpenFetchConfigurationException(IReadOnlyList<string> errors)
    : Exception("OpenFetch model is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
