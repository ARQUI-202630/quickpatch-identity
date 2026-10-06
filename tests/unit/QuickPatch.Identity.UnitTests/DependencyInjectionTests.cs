using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Identity.Application;
using QuickPatch.Identity.Infrastructure;

namespace QuickPatch.Identity.UnitTests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_RegistraLaCapaDeAplicacion()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddApplication());
    }

    [Fact]
    public void AddInfrastructure_RegistraLaCapaDeInfraestructura()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.Same(services, services.AddInfrastructure(configuration));
    }

    [Fact]
    public void AddApplication_SinServicios_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => QuickPatch.Identity.Application.DependencyInjection.AddApplication(null!));
    }

    [Fact]
    public void AddInfrastructure_SinConfiguracion_LanzaExcepcion()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddInfrastructure(null!));
    }
}