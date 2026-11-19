using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Recuperación de la contraseña olvidada (contrato 1.10.0, SCRUM-625): solicitudes pendientes para el administrador
/// (<c>password_recovery_requests</c>) y límite de solicitudes por correo (<c>password_recovery_attempts</c>), bajo RLS.
/// </summary>
public interface IPasswordRecoveryRepository
{
    /// <summary>
    /// Registra un intento para el correo si no pasa del límite en la ventana, contando también los correos sin cuenta.
    /// Serializa los intentos del mismo correo para que dos peticiones simultáneas no superen el límite. Devuelve
    /// <c>null</c> si se registró, o cuándo podrá pedirse otra vez.
    /// </summary>
    Task<DateTimeOffset?> TryRegisterAttemptAsync(
        Guid tenantId, string emailHash, DateTimeOffset now, int maxAttempts, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>La solicitud pendiente del usuario, bloqueada (<c>FOR UPDATE</c>).</summary>
    Task<PasswordRecoveryRequest?> FindPendingForUserForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>La solicitud por id, bloqueada; nula si no existe en el tenant de la sesión.</summary>
    Task<PasswordRecoveryRequest?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Solicitudes pendientes del tenant con los datos del usuario, de la más antigua a la más reciente.</summary>
    Task<PasswordRecoveryPage> ListPendingAsync(int page, int size, CancellationToken cancellationToken);

    void Add(PasswordRecoveryRequest request);
}

/// <summary>Solicitud pendiente tal como la ve el administrador (esquema <c>PasswordRecoveryPending</c>, contrato 1.10.0).</summary>
public sealed record PasswordRecoveryView(Guid Id, Guid UserId, string Email, string FullName, string Role, DateTimeOffset RequestedAt);

public sealed record PasswordRecoveryPage(IReadOnlyList<PasswordRecoveryView> Items, int Page, int Size, int TotalItems);