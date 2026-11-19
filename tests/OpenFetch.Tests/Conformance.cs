using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

/// <summary>
/// Data-driven conformance corpus (§29.1). valid/*.json must parse, plan and execute to the committed snapshots;
/// invalid/*.json must fail with the code and JSON Pointer in invalid/expected.json. Set OPENFETCH_UPDATE_SNAPSHOTS=1 to rewrite snapshots.
/// </summary>
public static class Corpus
{
    public static readonly string Root = FindRoot();
    public static readonly bool Update = Environment.GetEnvironmentVariable("OPENFETCH_UPDATE_SNAPSHOTS") == "1";

    public static TheoryData<string> Valid() => Cases("valid");
    public static TheoryData<string> Invalid() => Cases("invalid");

    private static TheoryData<string> Cases(string folder) =>
        [.. Directory.GetFiles(Path.Combine(Root, "conformance", folder), "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!).Where(n => n != "expected").Order(StringComparer.Ordinal)];

    public static string Read(string folder, string name) => File.ReadAllText(Path.Combine(Root, "conformance", folder, name + ".json"));

    public static void Snapshot(string folder, string file, string actual)
    {
        var path = Path.Combine(Root, "conformance", folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        actual = actual.ReplaceLineEndings("\n");
        if (Update || !File.Exists(path)) { File.WriteAllText(path, actual); return; }
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    public static string Dataset(string name) => name[..name.IndexOf('.')];

    /// <summary>Response JSON with run-specific values (cursor token, model hash) masked.</summary>
    public static string Result(ReadOnlySpan<byte> json)
    {
        var result = JsonNode.Parse(json)!;
        if (result["page"]!["endCursor"] is not null) result["page"]!["endCursor"] = "CURSOR";
        result["meta"]!["modelVersion"] = "MODEL";
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static string Plan(QueryPlan plan) => Command(plan.CommandText, plan.Parameters);

    /// <summary>Main plan plus each expansion's split query (for a single placeholder parent key).</summary>
    public static string Plan(PreparedQuery prepared, IQueryCompiler compiler)
    {
        var sb = new StringBuilder(Plan(prepared.Plan));
        foreach (var e in prepared.Query.Expansions)
        {
            var c = compiler.CompileExpansion(e, [Activator.CreateInstance(e.ParentKey.Column.ClrType)!]);
            sb.Append("-- expansion ").Append(e.Name).Append('\n').Append(Command(c.CommandText, c.Parameters));
        }
        return sb.ToString();
    }

    private static string Command(string text, IEnumerable<QueryParameter> parameters)
    {
        var sb = new StringBuilder(text).Append('\n');
        foreach (var p in parameters) sb.Append("-- @").Append(p.Name).Append(" = ").Append(Format(p.Value)).Append('\n');
        return sb.ToString();
    }

    private static string Format(object? v) => v switch
    {
        null => "NULL",
        Array a => a.GetType().GetElementType()!.Name + "[] {" + string.Join(", ", a.Cast<object?>().Select(Format)) + "}",
        DateTimeOffset d => "DateTimeOffset " + d.ToString("O", CultureInfo.InvariantCulture),
        _ => v.GetType().Name + " " + Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenFetch.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public class ConformanceTests
{
    private static readonly QueryEngine Engine = TestEngine.Create();

    [Theory, MemberData(nameof(Corpus.Valid), MemberType = typeof(Corpus))]
    public async Task Valid_case_matches_ast_and_plan(string name)
    {
        var json = Corpus.Read("valid", name);
        Corpus.Snapshot("expected-ast", name + ".json", AstWriter.ToJson(QueryParser.Parse(json, TestModel.Default.Limits)) + "\n");
        var prepared = await Engine.PrepareAsync(Corpus.Dataset(name), json, TestEngine.Context(SeedData.TenantA));
        Corpus.Snapshot("expected-plan", name + ".sql", Corpus.Plan(prepared, Engine.Compiler));
    }

    [Theory, MemberData(nameof(Corpus.Invalid), MemberType = typeof(Corpus))]
    public async Task Invalid_case_fails_with_expected_code_and_path(string name)
    {
        var expected = JsonNode.Parse(Corpus.Read("invalid", "expected"))![name]!;
        var ex = await Assert.ThrowsAsync<OpenFetchException>(async () =>
            await Engine.PrepareAsync(Corpus.Dataset(name), Corpus.Read("invalid", name), TestEngine.Context(SeedData.TenantA)));
        Assert.Equal(((string)expected["code"]!, (string?)expected["path"]), (ex.Code, ex.Path));
    }
}

[Collection("postgres")]
public class ConformanceResultTests(PostgresFixture pg)
{
    private static readonly QueryEngine Engine = TestEngine.Create();

    [Theory, MemberData(nameof(Corpus.Valid), MemberType = typeof(Corpus))]
    public async Task Valid_case_matches_expected_results(string name)
    {
        var context = TestEngine.Context(SeedData.TenantA);
        var prepared = await Engine.PrepareAsync(Corpus.Dataset(name), Corpus.Read("valid", name), context);
        var output = new ArrayBufferWriter<byte>();
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await Engine.ExecuteAsync(connection, prepared, context, output, TestContext.Current.CancellationToken);

        Corpus.Snapshot("expected-results", name + ".json", Corpus.Result(output.WrittenSpan));
    }
}
