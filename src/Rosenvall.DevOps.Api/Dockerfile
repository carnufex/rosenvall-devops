FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Rosenvall.DevOps.slnx ./
COPY src/Rosenvall.DevOps.Core/Rosenvall.DevOps.Core.csproj src/Rosenvall.DevOps.Core/
COPY src/Rosenvall.DevOps.Api/Rosenvall.DevOps.Api.csproj src/Rosenvall.DevOps.Api/
RUN dotnet restore src/Rosenvall.DevOps.Api/Rosenvall.DevOps.Api.csproj

COPY . .
RUN dotnet publish src/Rosenvall.DevOps.Api/Rosenvall.DevOps.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && curl -fsSLo /usr/local/bin/kubectl https://dl.k8s.io/release/v1.31.1/bin/linux/amd64/kubectl \
    && chmod 0755 /usr/local/bin/kubectl \
    && apt-get purge -y --auto-remove curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Rosenvall.DevOps.Api.dll"]
