FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY FirebirdMcp.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish FirebirdMcp.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .

# Stub FlameRobin config so the server starts without a local installation
RUN mkdir -p /root/.flamerobin && \
    echo '<?xml version="1.0" encoding="UTF-8"?><root/>' > /root/.flamerobin/fr_databases.conf

ENV Logging__LogLevel__Default=None

ENTRYPOINT ["dotnet", "FirebirdMcp.dll"]
