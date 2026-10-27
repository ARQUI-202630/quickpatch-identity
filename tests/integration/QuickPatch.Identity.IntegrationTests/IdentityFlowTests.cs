using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.Identity.Infrastructure.Persistence;

using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace QuickPatch.Identity.IntegrationTests;

/// <summary>
/// PostgreSQL 16 y Kafka reales, con las migraciones, los roles del DD 10.2 y el servicio conectado como
/// <c>identity_app</c> (sujeto a RLS). Hay un tenant activo (el del canal) y uno inactivo.
/// </summary>
public sealed class IdentityEnvironment : IAsyncLifetime
{
    public static readonly Guid ActiveTenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid InactiveTenant = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("db_identity").Build();
    private readonly KafkaContainer kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();
    private readonly string privateKey = RSA.Create(2048).ExportPkcs8PrivateKeyPem();

    public string AdminConnectionString => postgres.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(AdminConnectionString)
    {
        Username = "identity_app",
        Password = "app-pruebas",
    }.ConnectionString;

    public string KafkaServers => kafka.GetBootstrapAddress();

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public WebApplicationFactory<Program> AppFor(Guid channelTenant) =>
        App.WithWebHostBuilder(b => b.UseSetting("Channel:TenantId", channelTenant.ToString()));

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync());
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
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaServers }).Build())
        {
            await admin.CreateTopicsAsync([
                new TopicSpecification { Name = "technician.registered", NumPartitions = 1, ReplicationFactor = 1 },
                new TopicSpecification { Name = "technician.verification-changed", NumPartitions = 1, ReplicationFactor = 1 },
                new TopicSpecification { Name = "ranking.technician-reputation-changed", NumPartitions = 1, ReplicationFactor = 1 },
            ]);
        }

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Identity", AppConnectionString);
            b.UseSetting("Jwt:PrivateKeyPem", privateKey);
            b.UseSetting("Passwords:WorkFactor", "4");
            b.UseSetting("Channel:TenantId", ActiveTenant.ToString());
            b.UseSetting("DevelopmentUsers:Enabled", "false");
            b.UseSetting("Kafka:BootstrapServers", KafkaServers);
            b.UseSetting("Kafka:ConsumerGroupId", "identity-pruebas");
            b.UseSetting("Outbox:PublisherRole", "identity_outbox");
            b.UseSetting("Outbox:PollInterval", "00:00:00.200");
        });
        _ = App.Server;
    }

    public async Task DisposeAsync()
    {
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        await Task.WhenAll(postgres.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask());
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

    /// <summary>Lee de Kafka, en orden, hasta <paramref name="count"/> mensajes con la clave dada (máximo 30 s).</summary>
    public List<JsonElement> ConsumeAll(string topic, string key, int count)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = KafkaServers,
            GroupId = $"pruebas-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        var found = new List<JsonElement>();
        var limit = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limit && found.Count < count)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message.Key == key)
            {
                using var doc = JsonDocument.Parse(result.Message.Value);
                found.Add(doc.RootElement.Clone());
            }
        }

        consumer.Close();
        return found;
    }

    /// <summary>Lee de Kafka hasta encontrar el mensaje con la clave dada; null si no llega en 30 s.</summary>
    public (JsonElement Value, Headers Headers)? Consume(string topic, string key)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = KafkaServers,
            GroupId = $"pruebas-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);
        try
        {
            var limit = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < limit)
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message.Key == key)
                {
                    using var doc = JsonDocument.Parse(result.Message.Value);
                    return (doc.RootElement.Clone(), result.Message.Headers);
                }
            }

            return null;
        }
        finally
        {
            consumer.Close();
        }
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
    public async Task DosCanales_CadaUnoRegistraYAutenticaSoloEnSuTenant()
    {
        var gama = Guid.NewGuid();
        await env.ExecuteAdminAsync($"INSERT INTO tenants (id, name, nit, status, created_at) VALUES ('{gama}', 'Gama', '{Random.Shared.Next(100_000_000, 999_999_999)}-4', 'activo', now());");
        await using var app = env.App.WithWebHostBuilder(b =>
        {
            b.UseSetting("Channel:Tenants:canal-alfa", IdentityEnvironment.ActiveTenant.ToString());
            b.UseSetting("Channel:Tenants:canal-gama", gama.ToString());
        });
        using var client = app.CreateClient();
        var email = NewEmail();
        var soloAlfa = NewEmail();

        using var enAlfa = await client.SendAsync(WithChannel(Register, new { email, password = "Segura123", fullName = "Ana" }, "canal-alfa"));
        using var enGama = await client.SendAsync(WithChannel(Register, new { email, password = "Segura123", fullName = "Ana" }, "canal-gama"));
        using var otro = await client.SendAsync(WithChannel(Register, new { email = soloAlfa, password = "Segura123", fullName = "Beto" }, "canal-alfa"));
        using var cruzado = await client.SendAsync(WithChannel(Login, new { email = soloAlfa, password = "Segura123" }, "canal-gama"));
        using var ajeno = await client.SendAsync(WithChannel(Login, new { email = soloAlfa, password = "Segura123" }, "canal-ajeno"));

        Assert.Equal(HttpStatusCode.Created, enAlfa.StatusCode);
        Assert.Equal(HttpStatusCode.Created, enGama.StatusCode);
        Assert.Equal(HttpStatusCode.Created, otro.StatusCode);
        Assert.Equal(1L, await env.ScalarAdminAsync("SELECT count(*) FROM users WHERE email = @e AND tenant_id = @t", ("e", email), ("t", gama)));
        Assert.Equal(1L, await env.ScalarAdminAsync("SELECT count(*) FROM users WHERE email = @e AND tenant_id = @t", ("e", email), ("t", IdentityEnvironment.ActiveTenant)));
        Assert.Equal(HttpStatusCode.Unauthorized, cruzado.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ajeno.StatusCode);
    }

    [Fact]
    public async Task Rls_HabilitadoYForzadoEnCadaTablaConTenant()
    {
        // DD 10.1: toda tabla con tenant_id (y tenants, por su id) tiene RLS habilitado y forzado.
        var sinRls = await env.ScalarAdminAsync("""
            SELECT coalesce(string_agg(c.relname, ', ' ORDER BY c.relname), '')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
            WHERE c.relkind = 'r'
              AND (c.relname = 'tenants' OR EXISTS (
                    SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped))
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """);

        Assert.Equal(string.Empty, sinRls);
    }

    private static HttpRequestMessage WithChannel(Uri uri, object body, string channel)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Channel-Id", channel);
        return request;
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
    public async Task RegistroDeTecnico_PublicaTechnicianRegisteredEnKafka()
    {
        using var client = env.App.CreateClient();
        var correlation = Guid.NewGuid();
        var especialidad = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/auth/register/technician", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                email = NewEmail(),
                password = "Segura123",
                fullName = "Carlos Pérez",
                phone = "3001234567",
                documentId = Guid.NewGuid().ToString("N")[..12],
                specialtyId = especialidad,
                role = "tecnico",
            }),
        };
        request.Headers.Add("X-Correlation-Id", correlation.ToString());

        using var creado = await client.SendAsync(request);
        var id = (await creado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var mensaje = env.Consume("technician.registered", id.ToString());

        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        Assert.NotNull(mensaje);
        var (evento, headers) = mensaje.Value;
        var data = evento.GetProperty("data");
        Assert.Equal("technician.registered", evento.GetProperty("eventType").GetString());
        Assert.Equal(correlation, evento.GetProperty("correlationId").GetGuid());
        Assert.Equal(IdentityEnvironment.ActiveTenant, evento.GetProperty("tenantId").GetGuid());
        Assert.Equal("identity-service", evento.GetProperty("producer").GetString());
        Assert.Equal(id, data.GetProperty("technicianId").GetGuid());
        Assert.Equal(especialidad, data.GetProperty("specialtyId").GetGuid());
        Assert.Equal("pendiente", data.GetProperty("verificationStatus").GetString());
        Assert.Equal(evento.GetProperty("eventId").GetString(), Encoding.UTF8.GetString(headers.GetLastBytes("eventId")));

        // Outbox (ADR-007): la fila queda con el tenant del canal y el publicador la marca como publicada.
        var eventId = evento.GetProperty("eventId").GetGuid();
        Assert.Equal(IdentityEnvironment.ActiveTenant, (Guid)(await env.ScalarAdminAsync("SELECT tenant_id FROM outbox_events WHERE id = @e", ("e", eventId)))!);
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (await env.ScalarAdminAsync("SELECT published_at FROM outbox_events WHERE id = @e", ("e", eventId)) is DBNull && DateTime.UtcNow < limite)
        {
            await Task.Delay(100);
        }

        Assert.IsNotType<DBNull>(await env.ScalarAdminAsync("SELECT published_at FROM outbox_events WHERE id = @e", ("e", eventId)));
    }

    [Fact]
    public async Task Outbox_ElRolDelServicioNoPuedeLeerLosEventos()
    {
        await using var connection = new NpgsqlConnection(env.AppConnectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand("SELECT count(*) FROM outbox_events", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
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