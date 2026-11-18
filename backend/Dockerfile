# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first so the package layer is cached until the project file changes.
COPY src/LockDemo.Api/LockDemo.Api.csproj src/LockDemo.Api/
RUN dotnet restore src/LockDemo.Api/LockDemo.Api.csproj
COPY src/ src/
RUN dotnet publish src/LockDemo.Api/LockDemo.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app .
# Built-in non-root user of the .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "LockDemo.Api.dll"]
