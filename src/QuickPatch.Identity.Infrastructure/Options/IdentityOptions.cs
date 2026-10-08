namespace QuickPatch.Identity.Infrastructure.Options;

/// <summary>Emisión del JWT (sección <c>Jwt</c>). La llave privada llega como secreto, nunca en el repositorio.</summary>
public sealed class JwtIssuerOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "quickpatch-identity";

    public string Audience { get; set; } = "quickpatch";

    /// <summary>Llave privada RSA en PEM (PKCS#8 o PKCS#1).</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(60);
}

/// <summary>Hash de contraseñas (sección <c>Passwords</c>).</summary>
public sealed class PasswordOptions
{
    public const string Section = "Passwords";

    /// <summary>Factor de trabajo de BCrypt (2^n rondas).</summary>
    public int WorkFactor { get; set; } = 12;
}

/// <summary>Operaciones de plataforma (sección <c>Platform</c>, DD 10.4).</summary>
public sealed class PlatformOptions
{
    public const string Section = "Platform";

    /// <summary>Rol <c>BYPASSRLS</c> que adopta la transacción de plataforma con <c>SET LOCAL ROLE</c>.</summary>
    public string Role { get; set; } = "identity_platform";
}

/// <summary>
/// Tenant del canal (sección <c>Channel</c>). En el registro y el login el tenant se resuelve por el canal y no
/// por el cuerpo (RN-U5, DD 10.3). En el MVP opera un solo tenant, así que el canal es la instancia desplegada.
/// </summary>
public sealed class ChannelOptions
{
    public const string Section = "Channel";

    public Guid TenantId { get; set; }
}