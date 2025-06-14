FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PgDump.csproj", "PgDump/"]
RUN dotnet restore "PgDump/PgDump.csproj"
COPY . "PgDump/"
WORKDIR "/src/PgDump"
RUN dotnet build "PgDump.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "PgDump.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

FROM ubuntu:latest as linux


RUN apt update && apt  upgrade -y
RUN apt install -y ca-certificates
RUN apt install -y postgresql-common
RUN /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh
RUN apt install -y postgresql-client-17
RUN apt install -y dotnet-runtime-8.0
RUN update-ca-certificates
WORKDIR /app 
COPY --from=final /app /app

ENTRYPOINT ["dotnet", "PgDump.dll"]
