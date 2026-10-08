# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig dotnet-tools.json ./
COPY src/ src/
RUN dotnet publish "src/QuickPatch.Identity.Api/QuickPatch.Identity.Api.csproj" -c Release -o /app/publish
# Migraciones empaquetadas (EF Core migration bundle): el init container del despliegue las aplica con el rol
# identity_migrator antes de arrancar el servicio (deploy/k8s/identity.yaml).
RUN dotnet tool restore     && dotnet ef migrations bundle --project src/QuickPatch.Identity.Infrastructure/QuickPatch.Identity.Infrastructure.csproj        --configuration Release --self-contained --target-runtime linux-x64 --output /app/publish/efbundle --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Usuario sin privilegios que trae la imagen oficial.
USER app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "QuickPatch.Identity.Api.dll"]
