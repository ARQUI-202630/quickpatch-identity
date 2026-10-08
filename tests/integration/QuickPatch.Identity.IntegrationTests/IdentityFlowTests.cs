using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.Identity.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// PostgreSQL 16 real con las migraciones, los roles del DD 10.2 y el servicio conectado como <c>identity_app</c>
/// (sujeto a RLS). Hay un tenant activo (el del canal) y uno inactivo.
/// </summary>
public sealed class IdentityEnvironment : IAsyncLifetime
{
    public static readonly Guid ActiveTenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid InactiveTenant = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("db_identity").Build();
    private readonly string privateKey = RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    public string AdminConnectionString => postgres.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString)
    {
        Username = "identity_app",
        Password = "app-pruebas",
    }.ConnectionString;

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public WebApplicationFactory<Program> AppFor(Guid channelTenant) =>
        App.WithWebHostBuilder(b => b.UseSetting("Channel:TenantId", channelTenant.ToString()));

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(AdminConnectionString).Options;
        await using (var db = new IdentityDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await ExecuteAdminAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "roles.sql")));
        await ExecuteAdminAsync("ALTER ROLE identity_app LOGIN PASSWORD 'app-pruebas';");
        await ExecuteAdminAsync($"""
            INSERT INTO tenants (id, name, nit, status, created_at) VALUES
              ('{ActiveTenant}', 'Alfa', '900000001-1', 'activo', now()),
              ('{InactiveTenant}', 'Beta', '900000002-2', 'inactivo', now());
            """);

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Identity", AppConnectionString);
            b.UseSetting("Jwt:PrivateKeyPem", privateKey);
            b.UseSetting("Passwords:WorkFactor", "4");
            b.UseSetting("Channel:TenantId", ActiveTenant.ToString());
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        await postgres.DisposeAsync();
    }

    public async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAdminAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityDefinition : ICollectionFixture<IdentityEnvironment>
{
    public const string Name = "identity-real";
}

[Collection(IdentityDefinition.Name)]
public class IdentityFlowTests(IdentityEnvironment env)
{
    private static readonly Uri Register = new("/v1/auth/register/client", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);

    private static string NewEmail() => $"u{Guid.NewGuid():N}@correo.co";

    [Fact]
    public async Task HealthReady_ConPostgreSql_RespondeOk()
    {
        using var client = env.App.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Registro_GuardaBCryptEnElTenantDelCanal_YElCorreoRepetidoDa409()
    {
        using var client = env.App.CreateClient();
        var email = NewEmail();

        using var created = await client.PostAsJsonAsync(Register, new { email, password = "Segura123", fullName = "Ana Pérez" });
        using var repeated = await client.PostAsJsonAsync(Register, new { email, password = "Segura123", fullName = "Ana Pérez" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        var hash = (string)(await env.ScalarAdminAsync("SELECT password_hash FROM users WHERE email = @e", ("e", email)))!;
        Assert.Matches(@"^\$2[aby]\$\d{2}\$", hash);
        Assert.Equal(IdentityEnvironment.ActiveTenant, (Guid)(await env.ScalarAdminAsync("SELECT tenant_id FROM users WHERE email = @e", ("e", email)))!);
    }

    [Fact]
    public async Task QuintoFallo_Bloquea15MinutosEnLaBase()
    {
        using var client = env.App.CreateClient();
        var email = NewEmail();
        using (await client.PostAsJsonAsync(Register, new { email, password = "Segura123", fullName = "Ana" }))
        {
        }

        HttpStatusCode last = default;
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.PostAsJsonAsync(Login, new { email, password = "incorrecta" });
            last = response.StatusCode;
        }

        using var duringLock = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });
        var lockedUntil = (DateTime)(await env.ScalarAdminAsync("SELECT locked_until FROM users WHERE email = @e", ("e", email)))!;

        Assert.Equal(HttpStatusCode.Locked, last);
        Assert.Equal(HttpStatusCode.Locked, duringLock.StatusCode);
        Assert.InRange((lockedUntil.ToUniversalTime() - DateTime.UtcNow).TotalMinutes, 14, 15.1);
    }

    [Fact]
    public async Task TenantInactivo_Responde403TenantIsDisabled()
    {
        using var client = env.AppFor(IdentityEnvironment.InactiveTenant).CreateClient();
        var email = NewEmail();
        await env.ExecuteAdminAsync(
            "INSERT INTO users (id, tenant_id, email, password_hash, role, full_name, failed_login_attempts, created_at) " +
            $"VALUES ('{Guid.NewGuid()}', '{IdentityEnvironment.InactiveTenant}', '{email}', '{BCrypt.Net.BCrypt.HashPassword("Segura123", 4)}', 'cliente', 'Beto', 0, now())");
        using var registro = await client.PostAsJsonAsync(Register, new { email = NewEmail(), password = "Segura123", fullName = "Beto" });

        using var response = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });

        Assert.Equal(HttpStatusCode.Forbidden, registro.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenant is disabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UsuarioDeUnTenant_NoPuedeEntrarPorElCanalDeOtro()
    {
        using var clientA = env.App.CreateClient();
        var email = NewEmail();
        using (await clientA.PostAsJsonAsync(Register, new { email, password = "Segura123", fullName = "Ana" }))
        {
        }

        using var clientB = env.AppFor(IdentityEnvironment.InactiveTenant).CreateClient();
        using var response = await clientB.PostAsJsonAsync(Login, new { email, password = "Segura123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegistroDeTecnico_CreaPerfilPendiente_YElDocumentoEsUnicoPorTenant()
    {
        using var client = env.App.CreateClient();
        var documento = Guid.NewGuid().ToString("N")[..12];
        var especialidad = Guid.NewGuid();
        object Body(string email) => new { email, password = "Segura123", fullName = "Carlos Pérez", phone = "3001234567", documentId = documento, specialtyId = especialidad, role = "proveedor" };
        var email = NewEmail();

        using var creado = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), Body(email));
        using var repetido = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), Body(NewEmail()));
        var id = (await creado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal("pendiente", (string)(await env.ScalarAdminAsync("SELECT verification_status FROM technician_profiles WHERE user_id = @u", ("u", id)))!);
        Assert.Equal(especialidad, (Guid)(await env.ScalarAdminAsync("SELECT specialty_id FROM technician_profiles WHERE user_id = @u", ("u", id)))!);
        Assert.Equal(documento, (string)(await env.ScalarAdminAsync("SELECT document_id FROM users WHERE id = @u", ("u", id)))!);
    }

    [Fact]
    public async Task Rls_PerfilesDeTecnico_SoloVisiblesEnSuTenant()
    {
        using var client = env.App.CreateClient();
        using (await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), new
        {
            email = NewEmail(),
            password = "Segura123",
            fullName = "Carlos",
            phone = "3001234567",
            documentId = Guid.NewGuid().ToString("N")[..12],
            specialtyId = Guid.NewGuid(),
            role = "tecnico",
        }))
        {
        }

        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, IdentityEnvironment.InactiveTenant);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM technician_profiles", connection, transaction);

        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task LoginsFallidosEnParalelo_SeCuentanSinPerderIntentos()
    {
        using var client = env.App.CreateClient();
        var email = NewEmail();
        using (await client.PostAsJsonAsync(Register, new { email, password = "Segura123", fullName = "Ana" }))
        {
        }

        var intentos = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var c = env.App.CreateClient();
            using var r = await c.PostAsJsonAsync(Login, new { email, password = "incorrecta" });
            return r.StatusCode;
        });
        var codigos = await Task.WhenAll(intentos);

        Assert.Contains(HttpStatusCode.Locked, codigos);
        Assert.NotNull(await env.ScalarAdminAsync("SELECT locked_until FROM users WHERE email = @e AND locked_until IS NOT NULL", ("e", email)));
    }

    [Fact]
    public async Task Rls_ElRolDelServicioSoloVeSuTenant_YNoModificaTenants()
    {
        using var client = env.App.CreateClient();
        using (await client.PostAsJsonAsync(Register, new { email = NewEmail(), password = "Segura123", fullName = "Ana" }))
        {
        }

        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0L, await CountUsersAsync(connection, null));
        Assert.Equal(0L, await CountUsersAsync(connection, Guid.NewGuid()));
        Assert.True(await CountUsersAsync(connection, IdentityEnvironment.ActiveTenant) >= 1);

        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, IdentityEnvironment.ActiveTenant);
        await using var update = new NpgsqlCommand("UPDATE tenants SET status = 'activo'", connection, transaction);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Rls_InsertarUsuarioEnOtroTenant_EsRechazado()
    {
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, IdentityEnvironment.ActiveTenant);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO users (id, tenant_id, email, password_hash, role, full_name, failed_login_attempts, created_at) " +
            "VALUES (@id, @t, 'x@y.co', 'h', 'cliente', 'X', 0, now())",
            connection,
            transaction);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("t", IdentityEnvironment.InactiveTenant);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());

        Assert.Equal("42501", ex.SqlState);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant)
    {
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @t, true)", connection, transaction);
        set.Parameters.AddWithValue("t", tenant.ToString());
        await set.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountUsersAsync(NpgsqlConnection connection, Guid? tenant)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        if (tenant is not null)
        {
            await SetTenantAsync(connection, transaction, tenant.Value);
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM users", connection, transaction);
        var result = (long)(await count.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }
}