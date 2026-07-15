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
# Set per target platform by buildx (amd64, arm64, ...). Used below to install the x86-only GPU driver.
ARG TARGETARCH

RUN apt-get -o Acquire::Retries=5 update && apt-get -o Acquire::Retries=5 install -y --no-install-recommends \
    gosu \
    ffmpeg \
    libheif-examples \
    libheif-plugin-libde265 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd -g 888 appgroup \
    && useradd -u 888 -g 888 -d /data -s /bin/false appuser \
    && usermod -G users appuser

# Hardware video encoding (VAAPI) - amd64 only. Intel GPUs are x86, so the drivers exist only on amd64; on
# arm64 there's no Intel GPU and Boxy runs on the CPU (as it does whenever no usable GPU is found). apt
# skips the driver under --no-install-recommends, which is why ffmpeg had libva but nothing to load and
# VAAPI init failed with "unknown libva error". The base image is Ubuntu, where the Intel stack is in the
# already-enabled universe repo (no non-free/multiverse dance): iHD (intel-media-va-driver, current for
# Gen8+ incl. Gemini Lake) covers H.264 encode; i965 is the legacy Gen4-8 driver. libva picks the right one
# by PCI id; `vainfo` confirms it (VAEntrypointEncSlice = encode).
RUN if [ "${TARGETARCH}" = "amd64" ]; then \
      apt-get -o Acquire::Retries=5 update && apt-get -o Acquire::Retries=5 install -y --no-install-recommends \
         intel-media-va-driver i965-va-driver libva2 libva-drm2 vainfo \
      && rm -rf /var/lib/apt/lists/*; \
    fi

WORKDIR /app

COPY --from=build /app/publish ./
COPY Boxy.Web/entrypoint.sh ./entrypoint.sh
RUN chmod +x ./entrypoint.sh

VOLUME ["/data"]

ENV ASPNETCORE_HTTP_PORTS=3895

EXPOSE 3895

ENTRYPOINT ["./entrypoint.sh", "dotnet", "Boxy.Web.dll"]
