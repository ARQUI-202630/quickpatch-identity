using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Application.Abstractions;

/// <summary>
/// Invitaciones para definir la contraseña (<c>password_setup_tokens</c>, contrato 1.7.0) y, desde 1.10.0, enlaces de
/// recuperación, bajo RLS. Salvo <see cref="FindByHashForUpdateAsync"/>, cada método trabaja con un solo propósito.
/// </summary>
public interface IPasswordSetupTokenRepository
{
    /// <summary>Busca por el hash del token y bloquea la fila (<c>FOR UPDATE</c>) para que solo se use una vez.</summary>
    Task<PasswordSetupToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Tokens del usuario con ese propósito sin usar y sin vencer, bloqueados para anularlos.</summary>
    Task<IReadOnlyList<PasswordSetupToken>> ListUsableForUpdateAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Tokens del usuario con ese propósito que no se han usado, vencidos o no, bloqueados. La recuperación los marca
    /// como usados para que el usuario quede con la contraseña definida.
    /// </summary>
    Task<IReadOnlyList<PasswordSetupToken>> ListUnusedForUpdateAsync(Guid userId, string purpose, CancellationToken cancellationToken);

    /// <summary>Si el usuario ya definió su contraseña con alguna invitación.</summary>
    Task<bool> WasUsedAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Si el usuario tiene alguna invitación, usada o no. Quien no tiene ninguna definió su contraseña al registrarse.</summary>
    Task<bool> HasAnyAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PasswordSetupToken token);
}