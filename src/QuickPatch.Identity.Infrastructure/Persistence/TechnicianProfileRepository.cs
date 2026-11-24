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

public sealed class TechnicianProfileRepository(IdentityDbContext db) : ITechnicianProfileRepository
{
    public Task<TechnicianProfile?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.TechnicianProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

    public async Task<TechnicianProfile?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.TechnicianProfiles
            .FromSql($"SELECT * FROM technician_profiles WHERE user_id = {userId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    public async Task<TechnicianPage> ListAsync(
        string? verificationStatus, bool lowReputation, int page, int size, CancellationToken cancellationToken)
    {
        var profiles = db.TechnicianProfiles.AsNoTracking();
        if (verificationStatus is not null)
        {
            profiles = profiles.Where(x => x.VerificationStatus == verificationStatus);
        }

        if (lowReputation)
        {
            profiles = profiles.Where(x => x.LowReputation);
        }

        var query = profiles.Join(db.Users.AsNoTracking(), p => p.UserId, u => u.Id, (p, u) => new { Profile = p, User = u });

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Profile.CreatedAt)
            .ThenBy(x => x.Profile.UserId)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new AdminTechnicianView(
                x.User.Id,
                x.User.FullName,
                x.User.Email,
                x.User.Phone,
                x.User.Role,
                x.User.DocumentId!,
                x.Profile.SpecialtyId,
                x.Profile.VerificationStatus,
                x.Profile.VerificationReason,
                x.Profile.CreatedAt,
                x.Profile.AverageRating,
                x.Profile.RatingCount,
                x.Profile.LowReputation))
            .ToListAsync(cancellationToken);
        return new TechnicianPage(items, page, size, total);
    }

    public async Task<ProviderTechnicianPage> ListTeamAsync(Guid providerId, bool? active, int page, int size, CancellationToken cancellationToken)
    {
        var profiles = db.TechnicianProfiles.AsNoTracking().Where(x => x.ProviderId == providerId);
        profiles = active switch
        {
            true => profiles.Where(x => x.VerificationStatus == VerificationStatus.Aprobado),
            false => profiles.Where(x => x.VerificationStatus != VerificationStatus.Aprobado),
            null => profiles,
        };

        var query = profiles.Join(db.Users.AsNoTracking(), p => p.UserId, u => u.Id, (p, u) => new { Profile = p, User = u });

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Profile.CreatedAt)
            .ThenBy(x => x.Profile.UserId)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(x => new ProviderTechnicianView(x.User.Id, x.User.FullName, x.Profile.SpecialtyId, x.Profile.VerificationStatus))
            .ToListAsync(cancellationToken);
        return new ProviderTechnicianPage(items, page, size, total);
    }

    public void Add(TechnicianProfile profile) => db.TechnicianProfiles.Add(profile);
}