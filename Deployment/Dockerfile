# Multi-Stage Production Dockerfile for .NET 8 Minimal API Gateway
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["UnifiedGateway.csproj", "./"]
RUN dotnet restore "UnifiedGateway.csproj"

# Copy remaining source code and publish
COPY . .
RUN dotnet publish "UnifiedGateway.csproj" -c Release -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app

# Install ca-certificates and curl for health probes
RUN apk add --no-cache ca-certificates curl tzdata

# Create non-root user and persistent storage directory
RUN addgroup -S appgroup && adduser -S appuser -G appgroup
RUN mkdir -p /app/data /app/dataprotection-keys && chown -R appuser:appgroup /app

COPY --from=build --chown=appuser:appgroup /app/publish .

USER appuser

# Environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8080/gateway/health || exit 1

ENTRYPOINT ["dotnet", "UnifiedGateway.dll"]
