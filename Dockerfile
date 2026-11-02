# syntax=docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS source
ENV DOTNET_PROCESSOR_COUNT=2
WORKDIR /src
COPY . .

FROM source AS publish
RUN --mount=type=cache,id=bunkfy-nuget,target=/root/.nuget/packages \
    dotnet publish src/BunkFy.Host.Api/BunkFy.Host.Api.csproj -c Release -o /out/api --nologo \
    && dotnet publish src/BunkFy.Host.Worker/BunkFy.Host.Worker.csproj -c Release -o /out/worker --nologo \
    && dotnet publish src/BunkFy.Host.AdminApi/BunkFy.Host.AdminApi.csproj -c Release -o /out/admin-api --nologo \
    && dotnet publish src/BunkFy.Host.AdminCli/BunkFy.Host.AdminCli.csproj -c Release -o /out/admin-cli --nologo \
    && dotnet publish src/BunkFy.AdapterHost/BunkFy.AdapterHost.csproj -c Release -o /out/adapter-host --nologo \
    && dotnet publish src/BunkFy.Host.Migrations/BunkFy.Host.Migrations.csproj -c Release -o /out/migrations --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime-base
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/bunkfy/data-protection /var/lib/bunkfy/file-drop /var/lib/bunkfy/data-rights-ledger-delta \
    && chown -R app:app /var/lib/bunkfy
USER app
WORKDIR /app

FROM runtime-base AS backend
COPY --from=publish --chown=app:app /out /opt/bunkfy
WORKDIR /opt/bunkfy/api
EXPOSE 8080
ENTRYPOINT ["dotnet"]
CMD ["BunkFy.Host.Api.dll"]
