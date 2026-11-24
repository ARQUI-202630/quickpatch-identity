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
/// Transacción por tenant (DD 10.2) con la implementación común de ADR-020. Traduce los índices únicos del tenant a
/// las excepciones de dominio de Identity.
/// </summary>
public sealed class TenantUnitOfWork(IdentityDbContext db) : ITenantUnitOfWork
{
    private const string UniqueViolation = "23505";

    public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await TenantTransaction.ExecuteAsync(db, tenantId, work, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_users_tenant_email" })
        {
            throw new DuplicateEmailException("El correo ya está registrado en el tenant.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_users_tenant_document" })
        {
            throw new DuplicateDocumentException("El documento ya está registrado en el tenant.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: "ux_client_companies_tenant_nit" })
        {
            throw new DuplicateCompanyNitException("Ya existe una empresa con ese NIT en el tenant.", ex);
        }
    }
}