using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>API de Identity con los puertos en memoria y el emisor RS256 real.</summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    public static readonly Guid ActiveTenant = Guid.NewGuid();
    public static readonly Guid InactiveTenant = Guid.NewGuid();

    public IdentityApiFactory()
    {
        Store.Tenants.Add(new Tenant(ActiveTenant, "Alfa", "900", Tenant.Active, DateTimeOffset.UtcNow));
        Store.Tenants.Add(new Tenant(InactiveTenant, "Beta", "901", Tenant.Inactive, DateTimeOffset.UtcNow));
    }

    public InMemoryIdentityStore Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jwt:PrivateKeyPem", TestKeys.PrivateKeyPem);
        builder.UseSetting("Channel:TenantId", ActiveTenant.ToString());
        builder.UseSetting("ConnectionStrings:Identity", "Host=sin-base-en-pruebas-unitarias");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITenantUnitOfWork>(Store);
            services.AddSingleton<ITenantRepository>(Store);
            services.AddSingleton<IUserRepository>(Store);
            services.AddSingleton<ITechnicianProfileRepository>(Store);
            services.AddSingleton<IPlatformUnitOfWork>(Store);
            services.AddSingleton<IPlatformTenantRepository>(Store);
            services.AddSingleton<IAuditLog>(Store);
            services.AddSingleton<IPasswordHasher, FakeHasher>();
            services.Configure<HealthCheckServiceOptions>(o => o.Registrations.Clear());
        });
    }
}

public class IdentityApiTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly Uri Register = new("/v1/auth/register/client", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);
    private static readonly Uri Me = new("/v1/users/me", UriKind.Relative);

    private static string NewEmail() => $"u{Guid.NewGuid():N}@correo.co";

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        var email = NewEmail();
        using var response = await client.PostAsJsonAsync(Register, new { email, password = "Segura123", fullName = "Ana Pérez" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return email;
    }

    [Fact]
    public async Task Registrar_YLoguear_EmiteTokenRs256ConLosClaimsDelContrato()
    {
        using var client = factory.CreateClient();
        var email = await RegisterAsync(client);

        using var login = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await Json(login);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(3600, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal("cliente", body.GetProperty("user").GetProperty("role").GetString());
        var token = new JsonWebToken(body.GetProperty("accessToken").GetString());
        Assert.Equal("RS256", token.Alg);
        Assert.Equal(body.GetProperty("user").GetProperty("id").GetString(), token.Subject);
        Assert.Equal(IdentityApiFactory.ActiveTenant.ToString(), token.GetClaim("tenant_id").Value);
        Assert.Equal("cliente", token.GetClaim("role").Value);
        Assert.Equal("quickpatch-identity", token.Issuer);
    }

    [Fact]
    public async Task Me_ConElTokenEmitido_DevuelveElPerfil()
    {
        using var client = factory.CreateClient();
        var email = await RegisterAsync(client);
        using var login = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });
        var token = (await Json(login)).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var me = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(email, (await Json(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Me_SinTokenOTokenAlterado_Responde401()
    {
        using var client = factory.CreateClient();
        var email = await RegisterAsync(client);
        using var login = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });
        var token = (await Json(login)).GetProperty("accessToken").GetString()!;
        var alterado = token[..^4] + (token[^4] == 'A' ? "BBBB" : "AAAA");

        using var sinToken = await client.GetAsync(Me);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", alterado);
        using var conAlterado = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.Unauthorized, sinToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, conAlterado.StatusCode);
    }

    [Fact]
    public async Task Registrar_CorreoRepetido_Responde409()
    {
        using var client = factory.CreateClient();
        var email = await RegisterAsync(client);

        using var response = await client.PostAsJsonAsync(Register, new { email = email.ToUpperInvariant(), password = "Segura123", fullName = "Otra" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/correo-registrado", (await Json(response)).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("correo-malo", "Segura123")]
    [InlineData("ana@correo.co", "corta")]
    public async Task Registrar_DatosInvalidos_Responde400(string email, string password)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Register, new { email, password, fullName = "Ana Pérez" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/validacion", (await Json(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Registrar_ConTenantEnElCuerpo_Responde400()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(
            $$"""{"email":"{{NewEmail()}}","password":"Segura123","fullName":"Ana","tenantId":"{{Guid.NewGuid()}}"}""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync(Register, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ClaveIncorrecta_401_YQuintoFallo_423ConRetryAfter()
    {
        using var client = factory.CreateClient();
        var email = await RegisterAsync(client);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            using var fallo = await client.PostAsJsonAsync(Login, new { email, password = "incorrecta" });
            Assert.Equal(HttpStatusCode.Unauthorized, fallo.StatusCode);
        }

        last = await client.PostAsJsonAsync(Login, new { email, password = "incorrecta" });
        using var bloqueado = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });

        Assert.Equal(HttpStatusCode.Locked, last.StatusCode);
        Assert.True(int.Parse(last.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) > 800);
        Assert.Equal("https://quickpatch.internal/problems/cuenta-bloqueada", (await Json(last)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Locked, bloqueado.StatusCode);
        last.Dispose();
    }

    [Fact]
    public async Task Login_SinDatos_Responde400()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Login, new { email = "", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_TenantInactivo_Responde403TenantIsDisabled()
    {
        using var inactive = factory.WithWebHostBuilder(b => b.UseSetting("Channel:TenantId", IdentityApiFactory.InactiveTenant.ToString()));
        using var client = inactive.CreateClient();
        var email = NewEmail();
        factory.Store.Users.Add(QuickPatch.Identity.Domain.Users.User.Create(
            IdentityApiFactory.InactiveTenant, email, "hash:Segura123", "cliente", "Beto", null, DateTimeOffset.UtcNow));
        using var registro = await client.PostAsJsonAsync(Register, new { email = NewEmail(), password = "Segura123", fullName = "Beto" });

        using var response = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });

        Assert.Equal(HttpStatusCode.Forbidden, registro.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/tenant-inactivo", body.GetProperty("type").GetString());
        Assert.Equal("tenant is disabled", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task RegistrarTecnico_201ConPerfilPendiente_YElDocumentoRepetidoDa409()
    {
        using var client = factory.CreateClient();
        var documento = Guid.NewGuid().ToString("N")[..12];
        object Body(string email) => new { email, password = "Segura123", fullName = "Carlos Pérez", phone = "3001234567", documentId = documento, specialtyId = Guid.NewGuid(), role = "tecnico" };
        var email = NewEmail();

        using var creado = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), Body(email));
        using var repetido = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), Body(NewEmail()));
        using var login = await client.PostAsJsonAsync(Login, new { email, password = "Segura123" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Json(login)).GetProperty("accessToken").GetString());
        using var me = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        Assert.Equal("pendiente", (await Json(creado)).GetProperty("verificationStatus").GetString());
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/documento-registrado", (await Json(repetido)).GetProperty("type").GetString());
        Assert.Equal("pendiente", (await Json(me)).GetProperty("verificationStatus").GetString());
        Assert.Equal("tecnico", (await Json(me)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task RegistrarTecnico_DatosInvalidos_400_YSinCanal_503()
    {
        using var client = factory.CreateClient();
        using var sinCanal = factory.WithWebHostBuilder(b => b.UseSetting("Channel:TenantId", Guid.Empty.ToString()));
        using var clientSinCanal = sinCanal.CreateClient();
        var body = new { email = "malo", password = "Segura123", fullName = "Carlos", phone = "300", documentId = "1", specialtyId = Guid.Empty, role = "cliente" };

        using var invalido = await client.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), body);
        using var noCanal = await clientSinCanal.PostAsJsonAsync(new Uri("/v1/auth/register/technician", UriKind.Relative), body);

        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noCanal.StatusCode);
    }

    [Fact]
    public async Task CanalSinTenant_Responde503()
    {
        using var sinCanal = factory.WithWebHostBuilder(b => b.UseSetting("Channel:TenantId", Guid.Empty.ToString()));
        using var client = sinCanal.CreateClient();

        using var register = await client.PostAsJsonAsync(Register, new { email = NewEmail(), password = "Segura123", fullName = "Ana" });
        using var login = await client.PostAsJsonAsync(Login, new { email = NewEmail(), password = "Segura123" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, register.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
    }

    [Fact]
    public async Task Health_RespondeOk()
    {
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}

public class ForbiddenAuditHandlerTests
{
    [Fact]
    public async Task AccesoProhibido_SeRegistraComoWarning()
    {
        var logger = new ListLogger<ForbiddenAuditHandler>();
        var handler = new ForbiddenAuditHandler(logger);
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication("prueba").AddScheme<AuthenticationSchemeOptions, NoopAuthHandler>("prueba", _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "POST";
        context.Request.Path = "/v1/admin/tenants";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1"), new Claim("role", "cliente")], "test"));
        var policy = new AuthorizationPolicyBuilder().RequireRole("admin_plataforma").Build();

        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());
        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Success());

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("/v1/admin/tenants", entry.Message, StringComparison.Ordinal);
        Assert.Contains("cliente", entry.Message, StringComparison.Ordinal);
    }

    private sealed class NoopAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}