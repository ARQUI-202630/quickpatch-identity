using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

public interface IUserRepository
{
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<bool> DocumentExistsAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>
    /// Busca por correo y bloquea la fila (<c>FOR UPDATE</c>) hasta el fin de la transacción, para que dos logins
    /// simultáneos no cuenten mal los intentos fallidos (RN-U4).
    /// </summary>
    Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Usuarios del tenant de la sesión (RLS) con esos identificadores; los que no existen se omiten.</summary>
    Task<IReadOnlyList<User>> FindByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Busca por id y bloquea la fila (<c>FOR UPDATE</c>) para modificar al usuario en la transacción.</summary>
    Task<User?> FindByIdForUpdateAsync(Guid id, CancellationToken cancellationToken);

    void Add(User user);
}