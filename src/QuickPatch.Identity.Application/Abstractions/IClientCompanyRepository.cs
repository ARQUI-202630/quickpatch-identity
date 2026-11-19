using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>Empresas cliente (<c>client_companies</c>, DD 5.19), bajo RLS.</summary>
public interface IClientCompanyRepository
{
    Task<ClientCompany?> FindAsync(Guid companyId, CancellationToken cancellationToken);

    Task<bool> NitExistsAsync(string nit, CancellationToken cancellationToken);

    /// <summary>
    /// Usuarios de la empresa, del más antiguo al más reciente. <c>PasswordDefined</c> es falso mientras el usuario tenga
    /// invitaciones y no haya usado ninguna.
    /// </summary>
    Task<CompanyUserPage> ListUsersAsync(Guid companyId, int page, int size, CancellationToken cancellationToken);

    void Add(ClientCompany company);
}

/// <summary>Usuario de la empresa tal como lo ve otro usuario de ella (esquema <c>CompanyUser</c>, contrato 1.8.0).</summary>
public sealed record CompanyUserView(Guid UserId, string Email, string FullName, string? Phone, bool PasswordDefined, DateTimeOffset CreatedAt);

public sealed record CompanyUserPage(IReadOnlyList<CompanyUserView> Items, int Page, int Size, int TotalItems);