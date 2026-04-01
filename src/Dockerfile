# Build stage: compile StartupHook + stubs + tools
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder

# gcc needed for compiling Win32 P/Invoke stubs (kernel32_stubs.c → libwin32_stubs.so)
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /build
COPY src/ src/
RUN cd src/StartupHook && dotnet publish -c Release -o /build/output/hook

RUN cp /build/src/stubs/DrawingStub/bin/Release/net8.0/System.Drawing.Common.dll /build/output/DrawingStub.dll

# Publish tools
RUN cd src/tools/MergeNetstandard && dotnet publish -c Release -o /build/output/tools 2>&1 | tail -3
RUN cd src/tools/PatchNclTestPage && dotnet publish -c Release -o /build/output/tools/patcher 2>&1 | tail -3

# Copy .NET 8 reference assemblies from SDK (needed for Cecil type resolution)
RUN mkdir -p /build/output/refasm && \
    REF_DIR=$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -maxdepth 3 -type d -name "net8.0" | head -1) && \
    cp "$REF_DIR"/*.dll /build/output/refasm/ && \
    echo "Copied $(ls /build/output/refasm/*.dll | wc -l) reference assemblies"

# Runtime stage: minimal image
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Install tools for artifact download and SQL setup
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl unzip python3 gnupg && \
    curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql.list && \
    ACCEPT_EULA=Y apt-get update && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /bc/hook /bc/patched /bc/refasm /bc/addins-overlay /bc/testrunner /bc/service /bc/artifacts /bc/scripts /bc/tools

# Copy built artifacts from builder
COPY --from=builder /build/output/hook/ /bc/hook/
COPY --from=builder /build/output/refasm/ /bc/refasm/
COPY --from=builder /build/output/tools/ /bc/tools/
COPY --from=builder /build/output/DrawingStub.dll /bc/addins-overlay/System.Drawing.Common.dll

# Copy scripts
COPY scripts/ /bc/scripts/
RUN chmod +x /bc/scripts/*.sh

# Copy test runner extension (REST API for test execution)
COPY extensions/TestRunnerExtension/TestRunnerExtension.app /bc/testrunner/TestRunner.app

ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll
EXPOSE 7045-7054 7083 7085-7089

ENTRYPOINT ["/bc/scripts/entrypoint.sh"]
