# Multi-stage build: SDK image compiles/publishes the app, runtime image
# (much smaller, no build tools) is what actually ships and runs.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY K8sExplorer/K8sExplorer.csproj K8sExplorer/
RUN dotnet restore K8sExplorer/K8sExplorer.csproj

COPY K8sExplorer/ K8sExplorer/
RUN dotnet publish K8sExplorer/K8sExplorer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Spectre.Console needs a real terminal (see Program.cs) — run this image
# with `podman run -it` / `docker run -it`, not detached.
ENTRYPOINT ["dotnet", "K8sExplorer.dll"]
