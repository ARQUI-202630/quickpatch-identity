using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

namespace QuickPatch.Identity.IntegrationTests;

public class HealthEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_RespondeOk(string ruta)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(ruta, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}