# Multi-stage build: one `docker build -f Boxy.Web/Dockerfile -t boxy:latest .` from a clean checkout
# restores, publishes, and packages the app. The vendored front-end libs (wwwroot/lib) are committed,
# so no Node/LibMan step is needed.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on just the project files + central package list, so the layer caches until a
# dependency actually changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY Boxy.Data/Boxy.Data.csproj Boxy.Data/
COPY Boxy.Web/Boxy.Web.csproj Boxy.Web/
RUN dotnet restore Boxy.Web/Boxy.Web.csproj

# Then the sources, and publish.
COPY . .
RUN dotnet publish Boxy.Web/Boxy.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0

RUN apt-get update && apt-get install -y --no-install-recommends \
    gosu \
    ffmpeg \
    libheif-examples \
    libheif-plugin-libde265 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -g 888 appgroup \
    && useradd -u 888 -g 888 -d /data -s /bin/false appuser \
    && usermod -G users appuser

WORKDIR /app

COPY --from=build /app/publish ./
COPY Boxy.Web/entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

VOLUME ["/data"]

ENV ASPNETCORE_HTTP_PORTS=3895

EXPOSE 3895

ENTRYPOINT ["./entrypoint.sh", "dotnet", "Boxy.Web.dll"]
