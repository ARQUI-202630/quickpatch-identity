using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application.Abstractions;
using QuickPatch.Identity.Domain.Audit;
using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Domain.Users;
using QuickPatch.Identity.UnitTests.Support;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Recuperación de la contraseña olvidada (contrato identity 1.10.0, SCRUM-625).</summary>
public class PasswordRecoveryApiTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly Uri Request = new("/v1/auth/password-recovery", UriKind.Relative);
    private static readonly Uri Confirm = new("/v1/auth/password-recovery/confirm", UriKind.Relative);
    private static readonly Uri Admin = new("/v1/admin/password-recovery-requests", UriKind.Relative);
    private static readonly Uri Setup = new("/v1/auth/password/setup", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);

    private static Uri Link(Guid request) => new($"/v1/admin/password-recovery-requests/{request}/link", UriKind.Relative);

    private static string NewEmail() => $"r{Guid.NewGuid():N}@correo.co";

    private User Seed(string role = Roles.Cliente, Guid? tenant = null, string password = "Vieja2026", string? email = null)
    {
        var user = User.Create(tenant ?? IdentityApiFactory.ActiveTenant, email ?? NewEmail(), "hash:" + password, role, "Ana Gómez", null, DateTimeOffset.UtcNow);
        lock (factory.Store)
        {
            factory.Store.Users.Add(user);
        }

        return user;
    }

    private HttpClient As(User user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(user).AccessToken);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Pide la recuperación y devuelve el id de la solicitud que ve el administrador.</summary>
    private async Task<Guid> PedirAsync(HttpClient admin, User user)
    {
        using var anonimo = factory.CreateClient();
        using var pedido = await anonimo.PostAsJsonAsync(Request, new { email = user.Email });
        Assert.Equal(HttpStatusCode.Accepted, pedido.StatusCode);
        var lista = await Json(await admin.GetAsync(new Uri($"{Admin}?size=100", UriKind.Relative)));
        return lista.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("userId").GetGuid() == user.Id).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Flujo_PedirGenerarYConfirmar_CambiaLaContrasenaYDesbloquea()
    {
        var user = Seed(password: "Vieja2026");
        using var admin = As(Seed(Roles.AdminTenant));
        using var anonimo = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            using var fallo = await anonimo.PostAsJsonAsync(Login, new { email = user.Email, password = "Equivocada1" });
        }

        var solicitud = await PedirAsync(admin, user);
        using var enlace = await admin.PostAsync(Link(solicitud), null);
        Assert.Equal(HttpStatusCode.Created, enlace.StatusCode);
        var body = await Json(enlace);
        Assert.Equal(["link", "token", "expiresAt"], body.EnumerateObject().Select(p => p.Name));
        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        Assert.Equal($"https://quickpatch.internal/recuperar#token={token}", body.GetProperty("link").GetString());
        var expira = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expira - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));

        using var confirmar = await anonimo.PostAsJsonAsync(Confirm, new { token, password = "Nueva2026" });
        using var otraVez = await anonimo.PostAsJsonAsync(Confirm, new { token, password = "Otra20266" });
        using var conNueva = await anonimo.PostAsJsonAsync(Login, new { email = user.Email, password = "Nueva2026" });
        using var conVieja = await anonimo.PostAsJsonAsync(Login, new { email = user.Email, password = "Vieja2026" });
        using var cerrada = await admin.PostAsync(Link(solicitud), null);

        Assert.Equal(HttpStatusCode.NoContent, confirmar.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
        Assert.EndsWith("enlace-invalido", (await Json(otraVez)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, conNueva.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, conVieja.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, cerrada.StatusCode);
        Assert.EndsWith("transicion-invalida", (await Json(cerrada)).GetProperty("type").GetString(), StringComparison.Ordinal);

        // Solo el hash del token; la generación del enlace queda auditada sin el token.
        Assert.DoesNotContain(factory.Store.Invitations, i => i.TokenHash == token);
        var auditoria = Assert.Single(factory.Store.Audit, a => a.Action == AuditEntry.IssueRecoveryLink && a.TargetId == solicitud);
        Assert.DoesNotContain(token, auditoria.Metadata, StringComparison.Ordinal);
        var pendientes = await Json(await admin.GetAsync(new Uri($"{Admin}?size=100", UriKind.Relative)));
        Assert.DoesNotContain(pendientes.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == solicitud);
    }

    [Fact]
    public async Task MismaRespuesta_ExistaONoLaCuenta_YSinSolicitudParaAdministradores()
    {
        var adminTenant = Seed(Roles.AdminTenant);
        using var anonimo = factory.CreateClient();
        var antes = factory.Store.Recoveries.Count;

        using var inexistente = await anonimo.PostAsJsonAsync(Request, new { email = NewEmail() });
        using var deAdmin = await anonimo.PostAsJsonAsync(Request, new { email = adminTenant.Email });

        Assert.Equal(HttpStatusCode.Accepted, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, deAdmin.StatusCode);
        Assert.Equal(await inexistente.Content.ReadAsStringAsync(), await deAdmin.Content.ReadAsStringAsync());
        Assert.Equal(antes, factory.Store.Recoveries.Count);
    }

    [Fact]
    public async Task PedirloOtraVez_NoCreaOtraSolicitud_SoloActualizaLaFecha()
    {
        var user = Seed(Roles.Tecnico);
        using var admin = As(Seed(Roles.AdminTenant));

        var primera = await PedirAsync(admin, user);
        var segunda = await PedirAsync(admin, user);

        Assert.Equal(primera, segunda);
        Assert.Single(factory.Store.Recoveries, r => r.UserId == user.Id);
    }

    [Fact]
    public async Task CuartoPedidoEnLaHora_429ConRetryAfter_AunqueElCorreoNoExista()
    {
        using var anonimo = factory.CreateClient();
        var email = NewEmail();
        for (var i = 0; i < 3; i++)
        {
            using var ok = await anonimo.PostAsJsonAsync(Request, new { email });
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        // El límite es por correo: mayúsculas y espacios cuentan como el mismo.
        using var cuarto = await anonimo.PostAsJsonAsync(Request, new { email = $"  {email.ToUpperInvariant()} " });

        Assert.Equal(HttpStatusCode.TooManyRequests, cuarto.StatusCode);
        Assert.EndsWith("demasiadas-solicitudes", (await Json(cuarto)).GetProperty("type").GetString(), StringComparison.Ordinal);
        var retry = Assert.Single(cuarto.Headers.GetValues("Retry-After"));
        Assert.InRange(int.Parse(retry, System.Globalization.CultureInfo.InvariantCulture), 3500, 3600);

        // Solo se guarda el hash del correo.
        Assert.DoesNotContain(factory.Store.RecoveryAttempts, a => a.EmailHash.Contains('@', StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("no-es-correo")]
    [InlineData("")]
    public async Task CorreoInvalido_400ConElCampo(string email)
    {
        using var anonimo = factory.CreateClient();

        using var response = await anonimo.PostAsJsonAsync(Request, new { email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task EnlaceNuevo_AnulaElAnterior()
    {
        var user = Seed();
        using var admin = As(Seed(Roles.AdminTenant));
        var solicitud = await PedirAsync(admin, user);
        using var anonimo = factory.CreateClient();

        var viejo = (await Json(await admin.PostAsync(Link(solicitud), null))).GetProperty("token").GetString();
        var nuevo = (await Json(await admin.PostAsync(Link(solicitud), null))).GetProperty("token").GetString();
        using var conViejo = await anonimo.PostAsJsonAsync(Confirm, new { token = viejo, password = "Nueva2026" });
        using var conNuevo = await anonimo.PostAsJsonAsync(Confirm, new { token = nuevo, password = "Nueva2026" });

        Assert.Equal(HttpStatusCode.BadRequest, conViejo.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, conNuevo.StatusCode);
    }

    [Fact]
    public async Task AdminDeOtroTenant_NoVeLaSolicitud_Y404AlGenerarElEnlace()
    {
        var otroTenant = Guid.NewGuid();
        lock (factory.Store)
        {
            factory.Store.Tenants.Add(new Tenant(otroTenant, "Gamma", Guid.NewGuid().ToString("N")[..9], Tenant.Active, DateTimeOffset.UtcNow));
        }

        var user = Seed();
        using var admin = As(Seed(Roles.AdminTenant));
        using var ajeno = As(Seed(Roles.AdminTenant, otroTenant));
        var solicitud = await PedirAsync(admin, user);

        var lista = await Json(await ajeno.GetAsync(new Uri($"{Admin}?size=100", UriKind.Relative)));
        using var enlace = await ajeno.PostAsync(Link(solicitud), null);

        Assert.DoesNotContain(lista.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == solicitud);
        Assert.Equal(HttpStatusCode.NotFound, enlace.StatusCode);
    }

    [Theory]
    [InlineData(Roles.Cliente)]
    [InlineData(Roles.Tecnico)]
    [InlineData(Roles.Proveedor)]
    [InlineData(Roles.AdminPlataforma)]
    public async Task SoloAdminTenant_EnLaListaYElEnlace(string role)
    {
        using var http = As(Seed(role));

        using var lista = await http.GetAsync(Admin);
        using var enlace = await http.PostAsync(Link(Guid.NewGuid()), null);

        Assert.Equal(HttpStatusCode.Forbidden, lista.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, enlace.StatusCode);
    }

    [Fact]
    public async Task Lista_ConLosCamposDelContrato_YPaginaInvalida400()
    {
        var user = Seed(Roles.Proveedor);
        using var admin = As(Seed(Roles.AdminTenant));
        await PedirAsync(admin, user);

        var lista = await Json(await admin.GetAsync(new Uri($"{Admin}?size=100", UriKind.Relative)));
        var item = lista.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("userId").GetGuid() == user.Id);
        using var malo = await admin.GetAsync(new Uri($"{Admin}?size=101", UriKind.Relative));

        Assert.Equal(["items", "page", "size", "totalItems"], lista.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "userId", "email", "fullName", "role", "requestedAt"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(user.Email, item.GetProperty("email").GetString());
        Assert.Equal("proveedor", item.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, malo.StatusCode);
    }

    [Fact]
    public async Task CadaEnlace_SoloEnSuEndpoint()
    {
        var user = Seed();
        using var admin = As(Seed(Roles.AdminTenant));
        var solicitud = await PedirAsync(admin, user);
        var recuperacion = (await Json(await admin.PostAsync(Link(solicitud), null))).GetProperty("token").GetString()!;

        var invitado = Seed(Roles.Tecnico);
        PasswordSetupToken invitacion;
        string tokenInvitacion;
        lock (factory.Store)
        {
            invitacion = PasswordSetupToken.Issue(invitado, DateTimeOffset.UtcNow, out tokenInvitacion);
            factory.Store.Invitations.Add(invitacion);
        }

        using var anonimo = factory.CreateClient();
        using var recuperacionEnSetup = await anonimo.PostAsJsonAsync(Setup, new { token = recuperacion, password = "Nueva2026" });
        using var invitacionEnConfirm = await anonimo.PostAsJsonAsync(Confirm, new { token = tokenInvitacion, password = "Nueva2026" });

        Assert.Equal(HttpStatusCode.BadRequest, recuperacionEnSetup.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invitacionEnConfirm.StatusCode);
        Assert.Null(invitacion.UsedAt);
    }

    [Fact]
    public async Task ContrasenaCorta_400EnPassword()
    {
        using var anonimo = factory.CreateClient();

        using var response = await anonimo.PostAsJsonAsync(Confirm, new { token = "cualquiera", password = "corta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("password", out _));
    }
}