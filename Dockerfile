FROM debian:bookworm-slim
ENV DEBIAN_FRONTEND=noninteractive \
    Logging__LogLevel__Default=None
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl git && \
    apt-get clean && rm -rf /var/lib/apt/lists/*
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir /usr/local/dotnet && \
    ln -s /usr/local/dotnet/dotnet /usr/local/bin/dotnet
WORKDIR /app
COPY . .
RUN dotnet publish FirebirdMcp.csproj -c Release -o /app/publish && \
    mkdir -p /root/.flamerobin && \
    echo '<?xml version="1.0" encoding="UTF-8"?><root/>' > /root/.flamerobin/fr_databases.conf
CMD ["dotnet", "/app/publish/FirebirdMcp.dll"]
