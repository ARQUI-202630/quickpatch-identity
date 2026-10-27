using Microsoft.EntityFrameworkCore;

using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Companies;
using QuickPatch.Identity.Domain.Technicians;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.Infrastructure.Messaging;

namespace QuickPatch.Identity.Infrastructure.Persistence;

/// <summary>Base de datos de Identity Service (DD, secciones 5.1 y 5.2).</summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<ClientCompany> ClientCompanies => Set<ClientCompany>();

    public DbSet<TechnicianProfile> TechnicianProfiles => Set<TechnicianProfile>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<PasswordSetupToken> PasswordSetupTokens => Set<PasswordSetupToken>();

    public DbSet<OutboxEventRecord> OutboxEvents => Set<OutboxEventRecord>();

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
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique().HasDatabaseName("ux_users_tenant_email");
            e.HasIndex(x => new { x.TenantId, x.DocumentId }).IsUnique().HasFilter("document_id IS NOT NULL").HasDatabaseName("ux_users_tenant_document");
            e.HasIndex(x => new { x.TenantId, x.Role }).HasDatabaseName("ix_users_tenant_role");

            // Empresa cliente (DD 5.19, RF-06): FK física y RN-CC2 en la base.
            e.HasOne<ClientCompany>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_users_client_companies");
            e.HasIndex(x => x.CompanyId).HasDatabaseName("ix_users_company").HasFilter("company_id IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("ck_users_company_role", "(role = 'empresa_contacto') = (company_id IS NOT NULL)"));
        });

        modelBuilder.Entity<ClientCompany>(e =>
        {
            e.ToTable("client_companies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.LegalName).HasColumnName("legal_name").HasMaxLength(ClientCompany.LegalNameMaxLength);
            e.Property(x => x.Nit).HasColumnName("nit").HasMaxLength(ClientCompany.NitMaxLength);
            e.Property(x => x.ContactEmail).HasColumnName("contact_email").HasMaxLength(ClientCompany.ContactEmailMaxLength);
            e.Property(x => x.ContactPhone).HasColumnName("contact_phone").HasMaxLength(ClientCompany.ContactPhoneMaxLength);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.Nit }).IsUnique().HasDatabaseName("ux_client_companies_tenant_nit");
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
            e.Property(x => x.RatingCount).HasColumnName("rating_count");
            e.Property(x => x.LowReputation).HasColumnName("low_reputation");
            e.Property(x => x.ReputationChangedAt).HasColumnName("reputation_changed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<User>().WithOne().HasForeignKey<TechnicianProfile>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

            // Equipo del proveedor (P-02, RF-16): FK física al usuario proveedor dentro de Identity.
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_technician_profiles_provider");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_technician_profiles_tenant");
            e.HasIndex(x => new { x.TenantId, x.VerificationStatus }).HasDatabaseName("ix_technician_profiles_tenant_status");
            e.HasIndex(x => x.ProviderId).HasDatabaseName("ix_technician_profiles_provider").HasFilter("provider_id IS NOT NULL");
        });

        // Invitaciones del equipo del proveedor (DD 8.3.14, contrato 1.7.0): solo el hash del token.
        modelBuilder.Entity<PasswordSetupToken>(e =>
        {
            e.ToTable("password_setup_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsFixedLength();
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_password_setup_tokens_hash");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_password_setup_tokens_user");
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(100);
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_audit_logs_created_at");
            e.HasIndex(x => new { x.TenantId, x.Action }).HasDatabaseName("ix_audit_logs_tenant_action");
        });

        // Outbox (ADR-007, DD 5.15): mismo modelo que en los demás servicios.
        modelBuilder.Entity<OutboxEventRecord>(e =>
        {
            e.ToTable("outbox_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100);
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_outbox_events_pending").HasFilter("published_at IS NULL");
        });
    }
}