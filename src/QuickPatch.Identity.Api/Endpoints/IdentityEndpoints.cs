namespace QuickPatch.Identity.Api.Endpoints;

/// <summary>Endpoints del contrato <c>identity.v1.yaml</c>, uno por recurso (SCRUM-650).</summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapPasswordEndpoints();
        app.MapUserEndpoints();
        app.MapPlatformEndpoints();
        app.MapAdminTechnicianEndpoints();
        app.MapProviderTeamEndpoints();
        return app;
    }
}