# GTA Network master list. Build from the repository root:
#   docker build -f Tools/GTANetwork.Master/Dockerfile -t gtanetwork-master .
# Run:
#   docker run -d --name gtanetwork-master -p 8080:8080 -v gtanetwork-master-data:/data gtanetwork-master
# Put a reverse proxy with TLS in front (Caddy, nginx); the servers announce to https://<your host>/addserver and the
# clients read https://<your host>/servers. verified.txt and welcome.json live in /data.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Tools/GTANetwork.Master/GTANetwork.Master.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 MASTER_DATA=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "GTANetwork.Master.dll"]
