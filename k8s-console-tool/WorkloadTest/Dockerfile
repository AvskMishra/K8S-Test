FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY WorkloadTest/WorkloadTest.csproj WorkloadTest/
RUN dotnet restore WorkloadTest/WorkloadTest.csproj
COPY WorkloadTest/ WorkloadTest/
RUN dotnet publish WorkloadTest/WorkloadTest.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "WorkloadTest.dll"]
