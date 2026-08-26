# Build stage: compile StartupHook + stubs + tools
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:8.0-noble-amd64 AS builder

# gcc needed for compiling Win32 P/Invoke stubs (kernel32_stubs.c → libwin32_stubs.so);
# unzip for extracting libSkiaSharp.so out of NuGet packages (nupkg = zip)
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev unzip && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# .NET 10 SDK alongside the 8.0 SDK this image ships. BC 27/28 target net8.0,
# BC 29 targets net10.0, and the framework-impersonating stubs + Cecil reference
# assemblies have to be produced for BOTH. Deliberately installed on top of the
# noble-based sdk:8.0 image rather than switching the base to sdk:10.0
# libwin32_stubs.so is compiled here with gcc and has to keep running
# against the noble glibc in the aspnet:8.0 runtime stage.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet && \
    rm /tmp/dotnet-install.sh && \
    dotnet --list-sdks

WORKDIR /build
COPY src/ src/
COPY tools/ tools/
RUN cd src/StartupHook && dotnet publish -c Release -o /build/output/hook
# Web client startup hook (separate from the NST hook — see docs/WEBCLIENT-POC.md)
RUN cd src/WebClientHook && dotnet publish -c Release -o /build/output/webclient-hook

RUN cp /build/src/stubs/DrawingStub/bin/Release/net8.0/System.Drawing.Common.dll /build/output/DrawingStub.dll
RUN cp /build/src/stubs/DrawingStub/bin/Release/net10.0/System.Drawing.Common.dll /build/output/DrawingStub-net10.dll

# net10 flavour of every stub. StartupHook's BuildStubs target already built all
# target frameworks (the stub projects are multi-targeted); this only collects the
# net10.0 outputs into their own directory. The entrypoint overlays them onto
# /bc/hook when the BC build it is running targets net10.0 — see "runtime
# selection" there. Microsoft.Data.SqlClient is deliberately NOT duplicated: the
# package's newest asset is net8.0 and that assembly loads unmodified on .NET 10.
RUN mkdir -p /build/output/hook-net10 && \
    for p in GenevaStub:OpenTelemetry.Exporter.Geneva \
             DrawingStub:System.Drawing.Common \
             PerfCounterStub:System.Diagnostics.PerformanceCounter \
             WindowsPrincipalStub:System.Security.Principal.Windows \
             HttpSysStub:Microsoft.AspNetCore.Server.HttpSys; do \
      cp "/build/src/stubs/${p%%:*}/bin/Release/net10.0/${p##*:}.dll" /build/output/hook-net10/; \
    done && ls -la /build/output/hook-net10

# Publish tools
RUN cd src/tools/MergeNetstandard && dotnet publish -c Release -o /build/output/tools 2>&1 | tail -3
RUN cd src/tools/PatchNclTestPage && dotnet publish -c Release -o /build/output/tools/patcher 2>&1 | tail -3
# TestRunner — bundled in the image so run-tests.sh can invoke it via
# `docker compose exec` instead of `dotnet run`, removing the host-side
# .NET 8 SDK requirement for running tests against a Linux BC container.
RUN cd tools/TestRunner && dotnet publish -c Release -o /build/output/tools/TestRunner 2>&1 | tail -3

# Native libSkiaSharp.so for BC's report renderer, one per SkiaSharp line
# Microsoft has shipped (BC 28.x = 3.119.0; 2.88.9 covers the 2.88.x line).
# The entrypoint version-matches the artifact's managed SkiaSharp.dll and links
# the right one (plus the harfbuzz symlink) into the service dir — see the
# "report rendering natives" step there. nupkgs are plain zips.
RUN mkdir -p /build/output/skia && \
    for v in 2.88.9 3.119.0; do \
      curl -fsSL "https://www.nuget.org/api/v2/package/SkiaSharp.NativeAssets.Linux/$v" -o /tmp/skia.nupkg && \
      unzip -j -o /tmp/skia.nupkg "runtimes/linux-x64/native/libSkiaSharp.so" -d /build/output/skia/$v && \
      rm /tmp/skia.nupkg; \
    done && ls -la /build/output/skia/*

# OpenTelemetry exporters BC 29 references but Microsoft's artifact does not
# ship. BC 29's Microsoft.Dynamics.Nav.Ncl.dll references
# OpenTelemetry.Exporter.Console and .OpenTelemetryProtocol; neither DLL is
# anywhere in the platform artifact, so NavEnvironment's ctor dies with
# FileNotFoundException before the NST is up. BC 28's Ncl references neither,
# so the entrypoint only stages the ones actually referenced and missing.
# 1.15.3 = the OpenTelemetry version BC 29.0.53450 ships (read off the
# OpenTelemetry.dll in its own service tier). nupkgs are plain zips, and
# neither package has non-framework dependencies on net10.0.
RUN mkdir -p /build/output/otel && \
    for pkg in OpenTelemetry.Exporter.Console OpenTelemetry.Exporter.OpenTelemetryProtocol; do \
      curl -fsSL "https://www.nuget.org/api/v2/package/$pkg/1.15.3" -o /tmp/otel.nupkg && \
      unzip -j -o /tmp/otel.nupkg "lib/net10.0/$pkg.dll" -d /build/output/otel && \
      rm /tmp/otel.nupkg; \
    done && ls -la /build/output/otel

# Copy .NET reference assemblies from SDK (needed for Cecil type resolution).
# One set per runtime BC can target; the entrypoint picks by the framework version
# in Microsoft.Dynamics.Nav.Server.runtimeconfig.json.
RUN mkdir -p /build/output/refasm /build/output/refasm-net10 && \
    REF_DIR=$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -maxdepth 3 -type d -name "net8.0" | head -1) && \
    cp "$REF_DIR"/*.dll /build/output/refasm/ && \
    REF_DIR10=$(find /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref -maxdepth 3 -type d -name "net10.0" | head -1) && \
    cp "$REF_DIR10"/*.dll /build/output/refasm-net10/ && \
    echo "Copied $(ls /build/output/refasm/*.dll | wc -l) net8.0 + $(ls /build/output/refasm-net10/*.dll | wc -l) net10.0 reference assemblies"

# Runtime stage: minimal image
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:8.0-noble-amd64

# Install tools for artifact download and SQL setup + en_US locale.
# harfbuzz/freetype/fontconfig/fonts: BC's report renderer text-shapes via native
# P/Invoke ("harfbuzz", Aspose.Words) and rasterizes via SkiaSharp — without them
# every report render dies ("Text shaper factory failed to return text shaper").
# fonts-liberation is metric-compatible with Arial/Times, the RDLC defaults.
# NOTE: deliberately NO global libharfbuzz.so dev symlink here — harfbuzz must
# only become resolvable together with a version-matched libSkiaSharp.so (the
# entrypoint links both into the service dir, or neither): shaping-works-but-
# Skia-missing crashes the whole NST via SkiaSharp's SKObject finalizer, which
# is strictly worse than the graceful per-test shaper failure.
RUN apt-get update && apt-get install -y --no-install-recommends \
        curl unzip python3 gnupg locales \
        libharfbuzz0b libfreetype6 fontconfig fonts-liberation && \
    sed -i '/en_US.UTF-8/s/^# //' /etc/locale.gen && locale-gen && \
    curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg && \
    echo "deb [signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/mssql.list && \
    ACCEPT_EULA=Y apt-get update && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Second shared framework, side by side with the 8.0 one this base image ships.
# BC 27/28's Microsoft.Dynamics.Nav.Server.runtimeconfig.json asks for
# Microsoft.NETCore.App + Microsoft.AspNetCore.App 8.0.0; BC 29's asks for 10.0.0.
# Installing both means one image boots either — the host resolves the framework
# per app, and a net8.0 app does NOT roll forward to 10 while 8 is present.
# This is additive: nothing about the 8.0 path changes.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet && \
    rm /tmp/dotnet-install.sh && \
    dotnet --list-runtimes

RUN mkdir -p /bc/hook /bc/hook-net10 /bc/patched /bc/refasm /bc/refasm-net10 /bc/addins-overlay /bc/testrunner /bc/service /bc/artifacts /bc/scripts /bc/tools

# Copy built artifacts from builder
COPY --from=builder /build/output/hook/ /bc/hook/
COPY --from=builder /build/output/webclient-hook/ /bc/webclient-hook/
COPY --from=builder /build/output/hook-net10/ /bc/hook-net10/
COPY --from=builder /build/output/refasm/ /bc/refasm/
COPY --from=builder /build/output/refasm-net10/ /bc/refasm-net10/
COPY --from=builder /build/output/tools/ /bc/tools/
COPY --from=builder /build/output/DrawingStub.dll /bc/addins-overlay/System.Drawing.Common.dll
COPY --from=builder /build/output/DrawingStub-net10.dll /bc/addins-overlay/System.Drawing.Common.net10.dll
COPY --from=builder /build/output/skia/ /bc/natives/skia/
COPY --from=builder /build/output/otel/ /bc/natives/otel/

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
