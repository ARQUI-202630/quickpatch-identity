using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using QuickPatch.Identity.Api.Bootstrap;
using QuickPatch.Identity.Api.Endpoints;
using QuickPatch.Identity.Api.Http;
using QuickPatch.Identity.Api.Security;
using QuickPatch.Identity.Application;
using QuickPatch.Identity.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Logs en JSON con scopes (correlationId), para Loki (SCRUM-323) y la auditoría de 403 (RNF-04).
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // El contrato declara additionalProperties: false; un campo desconocido es un error de validación (400).
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddHealthChecks();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddQuickPatchAuthentication(builder.Configuration);

// Administrador inicial del tenant al desplegar (datos desde un secreto; vacío = no se crea).
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.Section));
builder.Services.AddHostedService<TenantAdminBootstrap>();

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Probes de k3s (Documento de Infraestructura, sección 5.8).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(QuickPatch.Identity.Infrastructure.DependencyInjection.ReadyTag),
});

app.MapIdentityEndpoints();

app.Run();

/// <summary>Punto de entrada; público para las pruebas con WebApplicationFactory.</summary>
public partial class Program;