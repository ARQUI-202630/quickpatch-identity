using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Options;
using QuickPatch.Transversal.Persistence;

namespace QuickPatch.Identity.Infrastructure.Persistence;

/// <summary>
/// Transacción de plataforma (DD 10.4): adopta <c>identity_platform</c> (<c>BYPASSRLS</c>) con <c>SET LOCAL ROLE</c>,
/// que dura solo esta transacción; fuera de ella el servicio sigue con <c>identity_app</c>, sujeto a RLS.
/// </summary>
public sealed class PlatformUnitOfWork(IdentityDbContext db, IOptions<PlatformOptions> options) : IPlatformUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        var role = options.Value.Role;
        if (!SessionRole.IsValidName(role))
        {
            // A diferencia de los demás roles, este es obligatorio: sin él no hay vista de plataforma.
            throw new InvalidOperationException("Platform:Role no es un nombre de rol válido.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await SessionRole.SetAsync(db, role, cancellationToken);

        var result = await work(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_tenants_nit" })
        {
            throw new DuplicateNitException("Ya existe un tenant con ese NIT.", ex);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}