using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Transacción de plataforma (DD 10.4): adopta el rol <c>identity_platform</c>, que no está limitado a un tenant,
/// solo para la gestión de tenants y su auditoría. Al terminar guarda los cambios y confirma.
/// </summary>
public interface IPlatformUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}