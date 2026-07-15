# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
COPY nuget.config .
COPY .dependencies/ .dependencies/
COPY Legacy.Maliev.FileService.Api/Legacy.Maliev.FileService.Api.csproj Legacy.Maliev.FileService.Api/
COPY Legacy.Maliev.FileService.Application/Legacy.Maliev.FileService.Application.csproj Legacy.Maliev.FileService.Application/
COPY Legacy.Maliev.FileService.Domain/Legacy.Maliev.FileService.Domain.csproj Legacy.Maliev.FileService.Domain/
COPY Legacy.Maliev.FileService.Data/Legacy.Maliev.FileService.Data.csproj Legacy.Maliev.FileService.Data/
RUN dotnet restore Legacy.Maliev.FileService.Api/Legacy.Maliev.FileService.Api.csproj \
    -p:MalievWorkspaceRoot=/src/.dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.FileService.Api/Legacy.Maliev.FileService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/src/.dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.FileService.Api.dll"]
