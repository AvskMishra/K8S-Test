FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY K8sExplorer/K8sExplorer.csproj K8sExplorer/
COPY PatchManager/PatchManager.csproj PatchManager/
RUN dotnet restore PatchManager/PatchManager.csproj
COPY K8sExplorer/ K8sExplorer/
COPY PatchManager/ PatchManager/
RUN dotnet publish PatchManager/PatchManager.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
USER 10001
ENTRYPOINT ["dotnet", "PatchManager.dll"]