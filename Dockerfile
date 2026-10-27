FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PaymentsLedger.Api/PaymentsLedger.Api.csproj src/PaymentsLedger.Api/
RUN dotnet restore src/PaymentsLedger.Api/PaymentsLedger.Api.csproj
COPY src/ src/
RUN dotnet publish src/PaymentsLedger.Api/PaymentsLedger.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentsLedger.Api.dll"]
