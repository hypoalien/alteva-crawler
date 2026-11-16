FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT=Crawler.Api
WORKDIR /source
COPY global.json Directory.Build.props Crawler.sln ./
COPY src/ ./src/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj --locked-mode
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG PROJECT=Crawler.Api
ENV CRAWLER_PROJECT=${PROJECT}
ENV ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app ./
USER app
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"${CRAWLER_PROJECT}.dll\" \"$@\"", "--"]
