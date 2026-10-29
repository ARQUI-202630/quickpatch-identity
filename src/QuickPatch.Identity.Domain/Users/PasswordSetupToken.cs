using System.Security.Cryptography;
using System.Text;

namespace QuickPatch.Identity.Domain.Users;

/// <summary>
/// Invitación para que un técnico del equipo de un proveedor defina su contraseña (DD 8.3.14, contrato 1.7.0) o, desde
/// el contrato 1.10.0, enlace de recuperación de la contraseña olvidada (SCRUM-625). Solo se guarda el hash SHA-256 del
/// token: el token en claro se entrega una vez y no puede recuperarse. Sirve una sola vez y vence según su propósito.
/// </summary>
public sealed class PasswordSetupToken
{
    /// <summary>Invitación para definir la contraseña por primera vez (contrato 1.7.0).</summary>
    public const string InvitationPurpose = "invitacion";

    /// <summary>Enlace de recuperación que genera el administrador del tenant (contrato 1.10.0).</summary>
    public const string RecoveryPurpose = "recuperacion";

    /// <summary>Vigencia de la invitación.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>Vigencia del enlace de recuperación (contrato 1.10.0).</summary>
    public static readonly TimeSpan RecoveryLifetime = TimeSpan.FromHours(1);

    private PasswordSetupToken(
        Guid id, Guid tenantId, Guid userId, string tokenHash, string purpose, DateTimeOffset expiresAt, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        Purpose = purpose;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid UserId { get; }

    /// <summary>SHA-256 del token en hexadecimal (64 caracteres).</summary>
    public string TokenHash { get; }

    /// <summary><see cref="InvitationPurpose"/> o <see cref="RecoveryPurpose"/>: cada flujo solo acepta los suyos.</summary>
    public string Purpose { get; }

    public bool IsRecovery => Purpose == RecoveryPurpose;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Cuándo se definió la contraseña con esta invitación; nulo mientras no se use.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Crea la invitación del usuario y devuelve el token en claro, que no se guarda.</summary>
    public static PasswordSetupToken Issue(User user, DateTimeOffset now, out string token)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Issue(user, InvitationPurpose, Lifetime, now, out token);
    }

    /// <summary>Crea el enlace de recuperación del usuario y devuelve el token en claro, que no se guarda.</summary>
    public static PasswordSetupToken IssueRecovery(User user, DateTimeOffset now, out string token)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Issue(user, RecoveryPurpose, RecoveryLifetime, now, out token);
    }

    private static PasswordSetupToken Issue(User user, string purpose, TimeSpan lifetime, DateTimeOffset now, out string token)
    {
        token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return new PasswordSetupToken(Guid.CreateVersion7(now), user.TenantId, user.Id, Hash(token), purpose, now.Add(lifetime), now);
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;

    /// <summary>Marca la invitación como usada al definir la contraseña.</summary>
    public void Consume(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new InvalidOperationException("La invitación ya se usó o venció.");
        }

        UsedAt = now;
    }

    /// <summary>Anula la invitación sin usarla (se generó otra o ya se definió la contraseña).</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (IsUsable(now))
        {
            ExpiresAt = now;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}