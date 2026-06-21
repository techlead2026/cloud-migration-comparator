# STAGE 1: Compile and Build the Application Context
FROM ://microsoft.com AS build-env
WORKDIR /app

# Copy the core identity file and restore StackExchange.Redis automatically
COPY *.csproj ./
RUN dotnet restore

# Copy all remaining C# source files and publish
COPY . ./
RUN dotnet publish -c Release -o out

# STAGE 2: Package the Lightweight Runtime Profile
FROM ://microsoft.com
WORKDIR /app

# Pull only the compiled compilation binaries from STAGE 1
COPY --from=build-env /app/out .

# Expose internal container web routing ports
EXPOSE 8080

# Execute the main application binary target
ENTRYPOINT ["dotnet", "CloudComparatorApi.dll"]
