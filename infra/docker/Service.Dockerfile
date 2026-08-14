# One image definition for every HTTP service. The service is selected by the
# SERVICE build argument, so adding a service means adding a compose entry, not
# another Dockerfile.
#
#   docker build -f infra/docker/Service.Dockerfile --build-arg SERVICE=Catalog .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
WORKDIR /src

# Restore from project files alone, so editing source does not invalidate the
# (slow) package restore layer.
COPY JameX.slnx ./
COPY src/shared/JameX.Contracts/JameX.Contracts.csproj             src/shared/JameX.Contracts/
COPY src/shared/JameX.ServiceDefaults/JameX.ServiceDefaults.csproj src/shared/JameX.ServiceDefaults/
COPY src/services/JameX.Gateway/JameX.Gateway.csproj               src/services/JameX.Gateway/
COPY src/services/JameX.Identity/JameX.Identity.csproj             src/services/JameX.Identity/
COPY src/services/JameX.Catalog/JameX.Catalog.csproj               src/services/JameX.Catalog/
COPY src/services/JameX.Ingest/JameX.Ingest.csproj                 src/services/JameX.Ingest/
COPY src/services/JameX.Encoder/JameX.Encoder.csproj               src/services/JameX.Encoder/
COPY src/services/JameX.Engagement/JameX.Engagement.csproj         src/services/JameX.Engagement/
COPY src/services/JameX.Search/JameX.Search.csproj                 src/services/JameX.Search/
RUN dotnet restore "src/services/JameX.${SERVICE}/JameX.${SERVICE}.csproj"

COPY src/ src/
RUN dotnet publish "src/services/JameX.${SERVICE}/JameX.${SERVICE}.csproj" \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SERVICE
WORKDIR /app
COPY --from=build /app ./

# ARG values do not survive into the running container, so carry it in an ENV.
ENV SERVICE_DLL="JameX.${SERVICE}.dll"
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# exec keeps dotnet as PID 1 so SIGTERM reaches it and shutdown is graceful —
# which matters for a consumer that must finish its in-flight message.
ENTRYPOINT ["sh", "-c", "exec dotnet $SERVICE_DLL"]
