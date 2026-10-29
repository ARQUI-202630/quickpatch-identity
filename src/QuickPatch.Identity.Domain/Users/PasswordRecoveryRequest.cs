namespace QuickPatch.Identity.Domain.Users;

/// <summary>
/// Solicitud de recuperación de la contraseña olvidada (contrato 1.10.0, SCRUM-625). El usuario la pide con su correo
/// y queda <see cref="Pending"/> para el <c>admin_tenant</c>, que genera el enlace. Un usuario tiene como máximo una
/// pendiente: si la pide de nuevo se actualiza <see cref="RequestedAt"/>. Se cierra cuando el usuario define la
/// contraseña nueva. No lleva ningún token.
/// </summary>
public sealed class PasswordRecoveryRequest
{
    public const string Pending = "pendiente";
    public const string Closed = "cerrada";

    /// <summary>Límite de solicitudes por correo, cuente o no con una cuenta (contrato 1.10.0).</summary>
    public const int MaxAttemptsPerWindow = 3;

    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(1);

    private PasswordRecoveryRequest(Guid id, Guid tenantId, Guid userId, string status, DateTimeOffset requestedAt, DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        Status = status;
        RequestedAt = requestedAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid UserId { get; }

    public string Status { get; private set; }

    /// <summary>Última vez que el usuario la pidió.</summary>
    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public bool IsPending => Status == Pending;

    /// <summary>
    /// Si el usuario puede recuperar su contraseña por este flujo. Los administradores no: la de un <c>admin_tenant</c>
    /// la recupera el <c>admin_plataforma</c> fuera de él (contrato 1.10.0).
    /// </summary>
    public static bool AppliesTo(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Role is not (Roles.AdminTenant or Roles.AdminPlataforma);
    }

    public static PasswordRecoveryRequest Open(User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!AppliesTo(user))
        {
            throw new InvalidOperationException("Los administradores no recuperan la contraseña por este flujo.");
        }

        return new PasswordRecoveryRequest(Guid.CreateVersion7(now), user.TenantId, user.Id, Pending, now, now);
    }

    /// <summary>El usuario la pidió otra vez mientras seguía pendiente.</summary>
    public void Renew(DateTimeOffset now)
    {
        if (IsPending)
        {
            RequestedAt = now;
        }
    }

    /// <summary>El usuario definió la contraseña nueva.</summary>
    public void Close(DateTimeOffset now)
    {
        if (IsPending)
        {
            Status = Closed;
            ClosedAt = now;
        }
    }
}