FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["AppleArt.csproj", "./"]
RUN dotnet restore "AppleArt.csproj"

COPY . .
RUN dotnet publish "AppleArt.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

RUN mkdir -p /app/data && mkdir -p /app/logs

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "AppleArt.dll"]