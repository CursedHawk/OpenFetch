using System.Security.Cryptography;
using System.Text;
using OpenFetch.Tests.Domain;

namespace OpenFetch.Tests;

public class CursorProfileTests
{
    private static readonly byte[] KeyA = Encoding.ASCII.GetBytes("openfetch-test-key-0123456789abcdef");
    private static readonly byte[] KeyB = Encoding.ASCII.GetBytes("openfetch-test-key-fedcba9876543210");

    /// <summary>Test vector published in docs/cursor-profile-v1.md.</summary>
    [Fact]
    public void Matches_the_documented_test_vector()
    {
        var protector = new SignedCursorProtector("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var payload = Encoding.UTF8.GetBytes("""{"m":"0123456789abcdef","s":"fedcba9876543210","c":"00112233445566aa","x":1800000000,"k":["2026-01-09T12:00:00Z","aaaaaaaa-1111-0000-0000-000000000009"]}""");
        var token = protector.Protect(payload);
        Assert.Equal(File.ReadAllText(Path.Combine(Corpus.Root, "docs", "cursor-profile-v1.md")).Split("```text\n")[1].Split('\n')[0], token);
        Assert.Equal(payload, protector.Unprotect(token));
    }

    [Fact]
    public void Rejects_tampering_unknown_keys_and_accepts_rotated_keys()
    {
        var old = new SignedCursorProtector("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var rotated = new SignedCursorProtector("k2", new Dictionary<string, byte[]> { ["k1"] = KeyA, ["k2"] = KeyB });
        var token = old.Protect([1, 2, 3]);
        Assert.Equal([1, 2, 3], rotated.Unprotect(token));          // old key still verifies
        Assert.StartsWith("of1.k2.", rotated.Protect([1]));          // new key signs

        var parts = token.Split('.');
        Assert.Throws<CryptographicException>(() => old.Unprotect($"{parts[0]}.{parts[1]}.{parts[2]}A.{parts[3]}"));
        Assert.Throws<CryptographicException>(() => old.Unprotect($"of1.zz.{parts[2]}.{parts[3]}"));
        Assert.Throws<CryptographicException>(() => new SignedCursorProtector("k2", new Dictionary<string, byte[]> { ["k2"] = KeyB }).Unprotect(token));
        Assert.Throws<ArgumentException>(() => new SignedCursorProtector("k", new Dictionary<string, byte[]> { ["k"] = new byte[16] }));
    }

    [Fact]
    public async Task Engine_uses_the_profile_end_to_end()
    {
        var protector = new SignedCursorProtector("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var engine = new QueryEngine(TestModel.Default, new Compiler.PostgreSql.PostgreSqlCompiler(), protector, new OpenFetchOptions().Security);
        var garbage = await Assert.ThrowsAsync<OpenFetchException>(async () =>
            await engine.PrepareAsync("orders", """{"select":["id"],"page":{"after":"of1.k1.e30.AAAA"}}""", TestEngine.Context(SeedData.TenantA)));
        Assert.Equal("invalid_cursor", garbage.Code);
    }
}
