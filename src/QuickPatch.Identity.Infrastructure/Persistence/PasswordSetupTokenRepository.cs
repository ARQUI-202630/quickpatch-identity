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

public sealed class PasswordSetupTokenRepository(IdentityDbContext db) : IPasswordSetupTokenRepository
{
    public async Task<PasswordSetupToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var rows = await db.PasswordSetupTokens
            .FromSql($"SELECT * FROM password_setup_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<PasswordSetupToken>> ListUsableForUpdateAsync(
        Guid userId, string purpose, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.PasswordSetupTokens
            .FromSql($"SELECT * FROM password_setup_tokens WHERE user_id = {userId} AND purpose = {purpose} AND used_at IS NULL AND expires_at > {now} FOR UPDATE")
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PasswordSetupToken>> ListUnusedForUpdateAsync(
        Guid userId, string purpose, CancellationToken cancellationToken) =>
        await db.PasswordSetupTokens
            .FromSql($"SELECT * FROM password_setup_tokens WHERE user_id = {userId} AND purpose = {purpose} AND used_at IS NULL FOR UPDATE")
            .ToListAsync(cancellationToken);

    // Solo invitaciones: un enlace de recuperación no dice si el usuario definió su contraseña al registrarse.
    public Task<bool> WasUsedAsync(Guid userId, CancellationToken cancellationToken) =>
        db.PasswordSetupTokens.AnyAsync(
            x => x.UserId == userId && x.Purpose == PasswordSetupToken.InvitationPurpose && x.UsedAt != null, cancellationToken);

    public Task<bool> HasAnyAsync(Guid userId, CancellationToken cancellationToken) =>
        db.PasswordSetupTokens.AnyAsync(x => x.UserId == userId && x.Purpose == PasswordSetupToken.InvitationPurpose, cancellationToken);

    public void Add(PasswordSetupToken token) => db.PasswordSetupTokens.Add(token);
}