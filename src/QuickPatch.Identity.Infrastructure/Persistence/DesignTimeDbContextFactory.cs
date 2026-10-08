using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuickPatch.Identity.Infrastructure.Persistence;

/// <summary>
/// Fábrica para <c>dotnet ef</c> (generar migraciones). No se conecta a ninguna base:
/// la conexión real la da el paso de despliegue con el rol de migraciones.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=db_identity")
            .Options;
        return new IdentityDbContext(options);
    }
}