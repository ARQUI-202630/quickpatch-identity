using System.Security.Cryptography;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.Infrastructure.Security;

/// <summary>BCrypt con factor de trabajo configurable (RN-U2, RNF-03; el TD IDN-015 exige BCrypt o Argon2).</summary>
public sealed class BCryptPasswordHasher(IOptions<PasswordOptions> options) : IPasswordHasher
{
    private readonly Lazy<string> dummyHash = new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), options.Value.WorkFactor));

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, options.Value.WorkFactor);

    public bool Verify(string password, string passwordHash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    public void SimulateVerify(string password) => _ = BCrypt.Net.BCrypt.Verify(password, dummyHash.Value);
}

/// <summary>
/// Firma el JWT con RS256 (llave privada solo en Identity). Claims: <c>sub</c>, <c>tenant_id</c>, <c>role</c>,
/// <c>iss</c>, <c>aud</c>, <c>exp</c> y <c>jti</c>.
/// </summary>
public sealed class RsaTokenIssuer : ITokenIssuer, IDisposable
{
    private readonly JwtIssuerOptions options;
    private readonly RSA rsa;
    private readonly SigningCredentials credentials;
    private readonly TimeProvider clock;

    public RsaTokenIssuer(IOptions<JwtIssuerOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.clock = clock;
        if (string.IsNullOrWhiteSpace(this.options.PrivateKeyPem))
        {
            throw new InvalidOperationException("Falta Jwt:PrivateKeyPem: Identity no puede emitir tokens.");
        }

        rsa = RSA.Create();
        rsa.ImportFromPem(this.options.PrivateKeyPem);
        credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
        ValidationKey = new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false));
    }

    /// <summary>Llave pública para validar los tokens emitidos.</summary>
    public SecurityKey ValidationKey { get; }

    /// <summary>Llave pública en PEM, la que reciben los demás servicios en <c>Jwt:PublicKeyPem</c>.</summary>
    public string PublicKeyPem => rsa.ExportSubjectPublicKeyInfoPem();

    public IssuedToken Issue(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = clock.GetUtcNow();
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(options.Lifetime).UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(),
                ["tenant_id"] = user.TenantId.ToString(),
                ["role"] = user.Role,
                ["jti"] = Guid.NewGuid().ToString(),
            },
        });

        return new IssuedToken(token, (int)options.Lifetime.TotalSeconds);
    }

    public void Dispose() => rsa.Dispose();
}