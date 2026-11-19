using System.Security.Cryptography;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Application.Technicians;
using QuickPatch.Identity.Domain.Common;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Companies;

/// <summary>
/// Empresa del usuario que llama, leída de la base y no del token: si el usuario no es <c>empresa_contacto</c> del
/// tenant de la transacción (RLS) no hay empresa.
/// </summary>
internal static class CompanyMembership
{
    public static async Task<User?> FindMemberAsync(IUserRepository users, Guid actorId, CancellationToken ct)
    {
        var actor = await users.FindByIdAsync(actorId, ct);
        return actor is { Role: Roles.EmpresaContacto, CompanyId: not null } ? actor : null;
    }
}