using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var options = JwtToolOptions.Parse(args);
if (options.LifetimeSeconds < 1)
{
    throw new InvalidOperationException("LifetimeSeconds must be greater than 0.");
}

Directory.CreateDirectory(Path.GetDirectoryName(options.PrivateKeyPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(options.PublicKeyPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(options.TokenOutputPath)!);

using var rsa = RSA.Create(2048);
if (options.GenerateKeyPair)
{
    if ((File.Exists(options.PrivateKeyPath) || File.Exists(options.PublicKeyPath)) && !options.Force)
    {
        throw new InvalidOperationException("Key files already exist. Use --force only after verifying replacement is intended.");
    }

    File.WriteAllText(options.PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
    File.WriteAllText(options.PublicKeyPath, rsa.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);
}
else
{
    if (!File.Exists(options.PrivateKeyPath))
    {
        throw new FileNotFoundException("Private signing key was not found. Generate a test key pair first.", options.PrivateKeyPath);
    }

    rsa.ImportFromPem(File.ReadAllText(options.PrivateKeyPath, Encoding.ASCII));
    if (!File.Exists(options.PublicKeyPath))
    {
        File.WriteAllText(options.PublicKeyPath, rsa.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);
    }
}

var now = DateTimeOffset.UtcNow;
var notBefore = now.AddSeconds(options.NotBeforeOffsetSeconds);
var expires = notBefore.AddSeconds(options.LifetimeSeconds);
var payload = new Dictionary<string, object>
{
    ["iss"] = options.Issuer,
    ["aud"] = options.Audience,
    ["sub"] = options.Subject,
    ["jti"] = Guid.NewGuid().ToString("N"),
    ["iat"] = now.ToUnixTimeSeconds(),
    ["nbf"] = notBefore.ToUnixTimeSeconds(),
    ["exp"] = expires.ToUnixTimeSeconds()
};
if (options.IncludeWorkstationId)
{
    payload["workstation_id"] = options.WorkstationId;
}
var header = new Dictionary<string, object>
{
    ["alg"] = "RS256",
    ["typ"] = "JWT"
};

var headerPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
var payloadPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
var signingInput = Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}");
var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var token = $"{headerPart}.{payloadPart}.{Base64Url(signature)}";
File.WriteAllText(options.TokenOutputPath, token, Encoding.ASCII);

Console.WriteLine($"Public key path: {options.PublicKeyPath}");
Console.WriteLine($"Token output path: {options.TokenOutputPath}");
Console.WriteLine($"Expires at UTC: {expires:O}");

static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

internal sealed record JwtToolOptions(
    string PrivateKeyPath,
    string PublicKeyPath,
    string TokenOutputPath,
    string Issuer,
    string Audience,
    string Subject,
    string WorkstationId,
    int LifetimeSeconds,
    int NotBeforeOffsetSeconds,
    bool IncludeWorkstationId,
    bool GenerateKeyPair,
    bool Force)
{
    public static JwtToolOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "--generate-key-pair" or "--force" or "--omit-workstation-id")
            {
                switches.Add(arg);
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Invalid argument: {arg}");
            }

            values[arg] = args[++index];
        }

        return new JwtToolOptions(
            Required(values, "--private-key"),
            Required(values, "--public-key"),
            Required(values, "--token-output"),
            values.GetValueOrDefault("--issuer", "thai-id-card-agent-client"),
            values.GetValueOrDefault("--audience", "thai-id-card-agent"),
            values.GetValueOrDefault("--subject", "operator-1"),
            values.GetValueOrDefault("--workstation-id", Environment.MachineName),
            int.Parse(values.GetValueOrDefault("--lifetime-seconds", "60")),
            int.Parse(values.GetValueOrDefault("--not-before-offset-seconds", "0")),
            !switches.Contains("--omit-workstation-id"),
            switches.Contains("--generate-key-pair"),
            switches.Contains("--force"));
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing required argument: {name}");
}
