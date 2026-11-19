using OpenFetch.Certification;

// openfetch-certify --url https://host [--prefix /query] --tenant-a "Authorization: Bearer A" --tenant-b "Authorization: Bearer B"
string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var url = Arg("--url");
var a = Arg("--tenant-a");
var b = Arg("--tenant-b");
if (url is null || a is null || b is null)
{
    Console.Error.WriteLine("usage: openfetch-certify --url <base url> --tenant-a \"Header: value\" --tenant-b \"Header: value\" [--prefix /query]");
    return 2;
}

static Action<System.Net.Http.Headers.HttpRequestHeaders> Header(string spec)
{
    var i = spec.IndexOf(':');
    var (name, value) = (spec[..i].Trim(), spec[(i + 1)..].Trim());
    return headers => headers.TryAddWithoutValidation(name, value);
}

using var client = new HttpClient { BaseAddress = new Uri(url) };
var target = new HttpCertificationTarget(client, new Dictionary<Caller, Action<System.Net.Http.Headers.HttpRequestHeaders>>
{
    [Caller.TenantA] = Header(a),
    [Caller.TenantB] = Header(b),
}, Arg("--prefix") ?? "/query");

var report = await CertificationSuite.RunAsync(target);
Console.WriteLine(report);
return report.Passed ? 0 : 1;
