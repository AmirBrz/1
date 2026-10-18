# Multi-stage: the review box needs only the runtime image; the SDK layer disappears with `--build`.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore layer first, keyed on the project files only: editing any .cs does not re-download packages.
COPY Directory.Build.props ./
COPY src/Samian.Domain/Samian.Domain.csproj src/Samian.Domain/
COPY src/Samian.Application/Samian.Application.csproj src/Samian.Application/
COPY src/Samian.Infrastructure/Samian.Infrastructure.csproj src/Samian.Infrastructure/
COPY src/Samian.WebApi/Samian.WebApi.csproj src/Samian.WebApi/
RUN dotnet restore src/Samian.WebApi/Samian.WebApi.csproj

COPY src/ src/
RUN dotnet publish src/Samian.WebApi/Samian.WebApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 80
ENTRYPOINT ["dotnet", "Samian.WebApi.dll"]