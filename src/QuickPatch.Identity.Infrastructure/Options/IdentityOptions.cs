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
/// Enlace de la invitación del equipo del proveedor (sección <c>Invitations</c>, contrato 1.7.0). El token va después
/// de <c>#</c>, así no llega a los registros de los servidores web cuando el técnico abre el enlace.
/// </summary>
public sealed class InvitationOptions
{
    public const string Section = "Invitations";

    /// <summary>Página o enlace profundo de la app que abre la pantalla para definir la contraseña.</summary>
    public string LinkBase { get; set; } = "https://quickpatch.internal/activar";

    public string LinkFor(string token) => $"{LinkBase.TrimEnd('#')}#token={token}";
}

/// <summary>
/// Tenant del canal (sección <c>Channel</c>). En el registro y el login el tenant se resuelve por el canal y no
/// por el cuerpo (RN-U5, DD 10.3). Cada canal (<c>X-Channel-Id</c>, DD 8.1) apunta a su tenant en <see cref="Tenants"/>
/// (DEP-09); sin cabecera se usa <see cref="TenantId"/>, el tenant de la instancia.
/// </summary>
public sealed class ChannelOptions
{
    public const string Section = "Channel";

    /// <summary>Tenant de las peticiones sin <c>X-Channel-Id</c> y de los usuarios iniciales.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Canal → tenant (<c>Channel__Tenants__quickpatch-mobile</c>). El canal no distingue mayúsculas.</summary>
    public Dictionary<string, Guid> Tenants { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tenant del canal: el del mapa si llega <paramref name="channelId"/>, o <see cref="TenantId"/> si no llega.
    /// Con el mapa vacío (un solo tenant, DD 10.3) todos los canales van a <see cref="TenantId"/>; con el mapa
    /// configurado, un canal que no está en él no cae en el tenant por defecto, para no registrar a nadie en otra empresa.
    /// </summary>
    public bool TryResolve(string? channelId, out Guid tenantId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || Tenants.Count == 0)
        {
            tenantId = TenantId;
        }
        else if (!Tenants.TryGetValue(channelId.Trim(), out tenantId))
        {
            tenantId = Guid.Empty;
        }

        return tenantId != Guid.Empty;
    }
}