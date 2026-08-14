# The encoder tier. Identical to Service.Dockerfile except that FFmpeg is baked
# into the runtime image — the transcode ladder shells out to it, so it is a
# hard dependency of the process rather than an optional tool.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY JameX.slnx ./
COPY src/shared/JameX.Contracts/JameX.Contracts.csproj             src/shared/JameX.Contracts/
COPY src/shared/JameX.ServiceDefaults/JameX.ServiceDefaults.csproj src/shared/JameX.ServiceDefaults/
COPY src/services/JameX.Encoder/JameX.Encoder.csproj               src/services/JameX.Encoder/
RUN dotnet restore src/services/JameX.Encoder/JameX.Encoder.csproj

COPY src/ src/
RUN dotnet publish src/services/JameX.Encoder/JameX.Encoder.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg \
 && rm -rf /var/lib/apt/lists/* \
 && ffmpeg -version | head -1

WORKDIR /app
COPY --from=build /app ./

# Scratch space for the raw download and the encoded ladder before upload.
# A real deployment would mount fast ephemeral storage here — transcoding is
# heavily disk-bound and this directory sees the full raw file plus every rung.
RUN mkdir -p /var/jamex/work
ENV Encoding__WorkDirectory=/var/jamex/work
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "JameX.Encoder.dll"]
