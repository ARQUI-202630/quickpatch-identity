using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;

using QuickPatch.Identity.Domain.Tenants;
using QuickPatch.Identity.Infrastructure.Options;

namespace QuickPatch.Identity.UnitTests.Api;

/// <summary>Tenant del registro y el login según <c>X-Channel-Id</c> (RN-U5, DEP-09, DD 10.3).</summary>
public class ChannelTenantTests(IdentityApiFactory factory) : IClassFixture<IdentityApiFactory>
{
    private static readonly Uri Register = new("/v1/auth/register/client", UriKind.Relative);
    private static readonly Uri Login = new("/v1/auth/login", UriKind.Relative);

    private static HttpRequestMessage Post(Uri uri, object body, string? channel)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
        if (channel is not null)
        {
            request.Headers.Add("X-Channel-Id", channel);
        }

        return request;
    }

    [Fact]
    public async Task CadaCanalRegistraYAutenticaEnSuTenant()
    {
        var gama = Guid.NewGuid();
        factory.Store.Tenants.Add(new Tenant(gama, "Gama", "902", Tenant.Active, DateTimeOffset.UtcNow));
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Channel:Tenants:canal-alfa", IdentityApiFactory.ActiveTenant.ToString());
            b.UseSetting("Channel:Tenants:canal-gama", gama.ToString());
        });
        using var client = app.CreateClient();
        var email = $"u{Guid.NewGuid():N}@correo.co";
        var soloAlfa = $"u{Guid.NewGuid():N}@correo.co";

        using var enAlfa = await client.SendAsync(Post(Register, new { email, password = "Segura123", fullName = "Ana" }, "canal-alfa"));
        using var enGama = await client.SendAsync(Post(Register, new { email, password = "Segura123", fullName = "Ana" }, "CANAL-GAMA"));
        using var otro = await client.SendAsync(Post(Register, new { email = soloAlfa, password = "Segura123", fullName = "Beto" }, "canal-alfa"));
        using var loginCruzado = await client.SendAsync(Post(Login, new { email = soloAlfa, password = "Segura123" }, "canal-gama"));
        using var loginPropio = await client.SendAsync(Post(Login, new { email = soloAlfa, password = "Segura123" }, "canal-alfa"));

        // El mismo correo vive en las dos empresas (RN-U1) y cada canal solo ve la suya.
        Assert.Equal(HttpStatusCode.Created, enAlfa.StatusCode);
        Assert.Equal(HttpStatusCode.Created, enGama.StatusCode);
        var tenants = factory.Store.Users.Where(u => u.Email == email).Select(u => u.TenantId).ToList();
        Assert.Equal(2, tenants.Count);
        Assert.Contains(IdentityApiFactory.ActiveTenant, tenants);
        Assert.Contains(gama, tenants);
        Assert.Equal(HttpStatusCode.Created, otro.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, loginCruzado.StatusCode);
        Assert.Equal(HttpStatusCode.OK, loginPropio.StatusCode);
    }

    [Fact]
    public async Task CanalDesconocido_503_YSinCabeceraUsaElTenantDeLaInstancia()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Channel:Tenants:canal-alfa", IdentityApiFactory.ActiveTenant.ToString()));
        using var client = app.CreateClient();
        var email = $"u{Guid.NewGuid():N}@correo.co";

        using var desconocido = await client.SendAsync(Post(Register, new { email, password = "Segura123", fullName = "Ana" }, "canal-ajeno"));
        using var sinCabecera = await client.SendAsync(Post(Register, new { email, password = "Segura123", fullName = "Ana" }, null));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, desconocido.StatusCode);
        Assert.Equal(HttpStatusCode.Created, sinCabecera.StatusCode);
        Assert.Contains(factory.Store.Users, u => u.Email == email && u.TenantId == IdentityApiFactory.ActiveTenant);
    }

    [Fact]
    public void MapaVacio_TodosLosCanalesVanAlTenantDeLaInstancia()
    {
        var tenant = Guid.NewGuid();
        var options = new ChannelOptions { TenantId = tenant };

        Assert.True(options.TryResolve("quickpatch-mobile", out var resuelto));
        Assert.Equal(tenant, resuelto);
        Assert.False(new ChannelOptions().TryResolve(null, out _));
    }

    [Theory]
    [InlineData("quickpatch-mobile", true)]
    [InlineData("  QuickPatch-Mobile ", true)]
    [InlineData("otro", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void MapaConfigurado_SoloResuelveLosCanalesDelMapa(string? canal, bool resuelve)
    {
        var porDefecto = Guid.NewGuid();
        var movil = Guid.NewGuid();
        var options = new ChannelOptions { TenantId = porDefecto };
        options.Tenants["quickpatch-mobile"] = movil;

        Assert.Equal(resuelve, options.TryResolve(canal, out var tenant));
        Assert.Equal(!resuelve ? Guid.Empty : string.IsNullOrWhiteSpace(canal) ? porDefecto : movil, tenant);
    }
}