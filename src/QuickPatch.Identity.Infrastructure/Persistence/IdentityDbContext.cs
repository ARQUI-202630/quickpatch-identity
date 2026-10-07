using Microsoft.EntityFrameworkCore;

using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;

namespace QuickPatch.Identity.Infrastructure.Persistence;

/// <summary>Base de datos de Identity Service (DD, secciones 5.1 y 5.2).</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<TechnicianProfile> TechnicianProfiles => Set<TechnicianProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(150);
            e.Property(x => x.Nit).HasColumnName("nit").HasMaxLength(30);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Nit).IsUnique().HasDatabaseName("ux_tenants_nit");
            e.HasIndex(x => x.Status).HasDatabaseName("ix_tenants_status");
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(User.EmailMaxLength);
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(30);
            e.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(User.FullNameMaxLength);
            e.Property(x => x.DocumentId).HasColumnName("document_id").HasMaxLength(User.DocumentMaxLength);
            e.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(User.PhoneMaxLength);
            e.Property(x => x.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique().HasDatabaseName("ux_users_tenant_email");
            e.HasIndex(x => new { x.TenantId, x.DocumentId }).IsUnique().HasFilter("document_id IS NOT NULL").HasDatabaseName("ux_users_tenant_document");
            e.HasIndex(x => new { x.TenantId, x.Role }).HasDatabaseName("ix_users_tenant_role");
        });

        modelBuilder.Entity<TechnicianProfile>(e =>
        {
            e.ToTable("technician_profiles");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.SpecialtyId).HasColumnName("specialty_id");
            e.Property(x => x.VerificationStatus).HasColumnName("verification_status").HasMaxLength(25);
            e.Property(x => x.VerificationReason).HasColumnName("verification_reason").HasColumnType("text");
            e.Property(x => x.AverageRating).HasColumnName("average_rating").HasPrecision(2, 1);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<User>().WithOne().HasForeignKey<TechnicianProfile>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_technician_profiles_tenant");
            e.HasIndex(x => new { x.TenantId, x.VerificationStatus }).HasDatabaseName("ix_technician_profiles_tenant_status");
        });
    }
}