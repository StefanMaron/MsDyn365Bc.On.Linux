# Build stage: compile StartupHook + stubs + tools
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:8.0 AS builder

# gcc needed for compiling Win32 P/Invoke stubs (kernel32_stubs.c → libwin32_stubs.so)
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /build
COPY src/ src/
COPY tools/ tools/
RUN cd src/StartupHook && dotnet publish -c Release -o /build/output/hook
# Web client startup hook (separate from the NST hook — see docs/WEBCLIENT-POC.md)
RUN cd src/WebClientHook && dotnet publish -c Release -o /build/output/webclient-hook

RUN cp /build/src/stubs/DrawingStub/bin/Release/net8.0/System.Drawing.Common.dll /build/output/DrawingStub.dll

# Publish tools
RUN cd src/tools/MergeNetstandard && dotnet publish -c Release -o /build/output/tools 2>&1 | tail -3
RUN cd src/tools/PatchNclTestPage && dotnet publish -c Release -o /build/output/tools/patcher 2>&1 | tail -3
# TestRunner — bundled in the image so run-tests.sh can invoke it via
# `docker compose exec` instead of `dotnet run`, removing the host-side
# .NET 8 SDK requirement for running tests against a Linux BC container.
RUN cd tools/TestRunner && dotnet publish -c Release -o /build/output/tools/TestRunner 2>&1 | tail -3

# Copy .NET 8 reference assemblies from SDK (needed for Cecil type resolution)
RUN mkdir -p /build/output/refasm && \
    REF_DIR=$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -maxdepth 3 -type d -name "net8.0" | head -1) && \
    cp "$REF_DIR"/*.dll /build/output/refasm/ && \
    echo "Copied $(ls /build/output/refasm/*.dll | wc -l) reference assemblies"

# Runtime stage: minimal image
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:8.0

# --- Toggleable image-size optimizations (default 0 = current behavior) -------
# See OPTIMIZATION-FLAGS.md. Toggle with e.g.:
#   OPT_GO_SQLCMD=1 docker compose build bc
# OPT_GO_SQLCMD=1 : install the standalone go-sqlcmd binary instead of the
#   mssql-tools18 apt package (no MS apt repo, no gnupg, no unixODBC/msodbcsql18).
#   Biggest single image shrink. EXPERIMENTAL — flag compatibility could not be
#   boot-validated here; smoke-test SQL setup before relying on it. If a server
#   needs different TLS flags, override SQLCMD_TLS at runtime (see entrypoint).
# OPT_DROP_GNUPG=1: (only relevant when OPT_GO_SQLCMD=0) read the Microsoft apt
#   key as an ASCII-armored (.asc) keyring via signed-by= instead of dearmoring
#   it with gpg, so gnupg and its deps don't ship in the runtime image.
ARG OPT_GO_SQLCMD=0
ARG OPT_DROP_GNUPG=0
ARG GO_SQLCMD_URL=https://github.com/microsoft/go-sqlcmd/releases/download/v1.8.2/sqlcmd-linux-amd64.tar.bz2

# Base tools + en_US locale (always needed)
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl unzip python3 locales && \
    sed -i '/en_US.UTF-8/s/^# //' /etc/locale.gen && locale-gen && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# SQL client: go-sqlcmd binary (smaller) or mssql-tools18 (current default).
# Either way a `sqlcmd` shim lands on PATH at /usr/local/bin and the chosen
# flavor is recorded in /etc/bc-sqlcmd-flavor so the entrypoint can pick the
# right TLS flags.
RUN set -eu; \
    if [ "$OPT_GO_SQLCMD" = "1" ]; then \
        echo "[build] SQL client = go-sqlcmd ($GO_SQLCMD_URL)"; \
        mkdir -p /opt/sqlcmd; \
        apt-get update; apt-get install -y --no-install-recommends bzip2; \
        curl -fsSL "$GO_SQLCMD_URL" -o /tmp/sqlcmd.tar.bz2; \
        tar -xjf /tmp/sqlcmd.tar.bz2 -C /opt/sqlcmd sqlcmd; \
        rm -f /tmp/sqlcmd.tar.bz2; \
        apt-get purge -y bzip2; apt-get autoremove -y; \
        ln -sf /opt/sqlcmd/sqlcmd /usr/local/bin/sqlcmd; \
        echo go > /etc/bc-sqlcmd-flavor; \
    else \
        echo "[build] SQL client = mssql-tools18 (drop-gnupg=$OPT_DROP_GNUPG)"; \
        if [ "$OPT_DROP_GNUPG" = "1" ]; then \
            curl -fsSL https://packages.microsoft.com/keys/microsoft.asc -o /usr/share/keyrings/microsoft.asc; \
            echo "deb [signed-by=/usr/share/keyrings/microsoft.asc] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql.list; \
        else \
            apt-get update; apt-get install -y --no-install-recommends gnupg; \
            curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg; \
            echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/debian/12/prod bookworm main" > /etc/apt/sources.list.d/mssql.list; \
        fi; \
        ACCEPT_EULA=Y apt-get update; ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18; \
        ln -sf /opt/mssql-tools18/bin/sqlcmd /usr/local/bin/sqlcmd; \
        echo odbc > /etc/bc-sqlcmd-flavor; \
    fi; \
    apt-get clean; rm -rf /var/lib/apt/lists/*

RUN mkdir -p /bc/hook /bc/patched /bc/refasm /bc/addins-overlay /bc/testrunner /bc/service /bc/artifacts /bc/scripts /bc/tools

# Copy built artifacts from builder
COPY --from=builder /build/output/hook/ /bc/hook/
COPY --from=builder /build/output/webclient-hook/ /bc/webclient-hook/
COPY --from=builder /build/output/refasm/ /bc/refasm/
COPY --from=builder /build/output/tools/ /bc/tools/
COPY --from=builder /build/output/DrawingStub.dll /bc/addins-overlay/System.Drawing.Common.dll

# Copy scripts
COPY scripts/ /bc/scripts/
RUN chmod +x /bc/scripts/*.sh

# Copy test runner extension (REST API for test execution)
COPY extensions/TestRunnerExtension/TestRunnerExtension.app /bc/testrunner/TestRunner.app

# Copy SideService stubs (Linux replacements for Windows PE executables)
COPY stubs/reporting-service-stub /bc/stubs/reporting-service-stub
COPY stubs/reporting-service-stub.dll /bc/stubs/reporting-service-stub.dll
COPY stubs/reporting-service-stub.runtimeconfig.json /bc/stubs/reporting-service-stub.runtimeconfig.json

ENV DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll \
    LANG=en_US.UTF-8 \
    LC_ALL=en_US.UTF-8
EXPOSE 7045-7054 7083 7085-7089

ENTRYPOINT ["/bc/scripts/entrypoint.sh"]
