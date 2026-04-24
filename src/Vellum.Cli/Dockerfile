# Vellum.Cli Linux test harness.
#
# Builds the CLI from local source (ProjectReference to ../Vellum), so any
# edits under src/Vellum/ flow through without a NuGet publish round-trip.
# Use it to reproduce and iterate on Linux-specific Skia behaviour against
# the exact Debian base that cortex runs in production.
#
# Build (from repo root = C:\repos\lotane\ocr_playground):
#   docker build -t vellum-cli -f src/Vellum.Cli/Dockerfile .
#
# One-time model download into a persistent named volume (~121 MB).
# The volume mounts at /root/.local/share/vellum because that's where
# `vellum download` writes by default AND where ScreenAI.FindScreenAiDir
# looks on Linux — so download + ocr use the same path with no plumbing.
#   docker volume create vellum-models
#   docker run --rm -v vellum-models:/root/.local/share/vellum \
#     vellum-cli download
#
# OCR a PDF (reuses the persisted model — no re-download):
#   docker run --rm \
#     -v vellum-models:/root/.local/share/vellum \
#     -v "C:/path/to/sample.pdf:/work/input.pdf:ro" \
#     vellum-cli ocr /work/input.pdf --text
#
# Also dump raw + post-composite PNGs for inspection:
#   docker run --rm \
#     -v vellum-models:/root/.local/share/vellum \
#     -v "C:/path/to/sample.pdf:/work/input.pdf:ro" \
#     -v "C:/path/to/dump:/work/dump" \
#     -e VELLUM_RASTER_DUMP=/work/dump \
#     vellum-cli ocr /work/input.pdf --text

# -------- Build stage.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# build-essential (gcc + libc6-dev + ...) is needed for the chromium_stubs.so
# compile. gcc alone lacks crti.o.
RUN apt-get update \
 && apt-get install -y --no-install-recommends build-essential \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY Directory.Build.props ./
COPY src/Vellum/ src/Vellum/
COPY src/Vellum.Cli/ src/Vellum.Cli/
COPY src/native/ src/native/

# Pre-compile the chromium stubs shim BEFORE dotnet publish.
# Vellum.csproj's <None Include="..._chromium_stubs.so" Condition="Exists(...)">
# is evaluated at project-load time, before the BuildChromiumStubs target runs
# — so if the .so doesn't already exist when publish starts, it never gets
# copied to the publish output even if the target successfully compiles it.
RUN mkdir -p src/Vellum/runtimes/linux-x64/native \
 && cc -shared -fPIC \
      -o src/Vellum/runtimes/linux-x64/native/_chromium_stubs.so \
      src/native/chromium_stubs/stubs.c \
 && test -f src/Vellum/runtimes/linux-x64/native/_chromium_stubs.so

RUN dotnet publish src/Vellum.Cli/Vellum.Cli.csproj \
      -c Release -r linux-x64 --self-contained false \
      -o /app/vellum

# -------- Runtime stage.
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime

# SkiaSharp.NativeAssets.Linux (non-NoDependencies variant) requires fontconfig.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /work
COPY --from=build /app/vellum /app/vellum

ENTRYPOINT ["dotnet", "/app/vellum/Vellum.Cli.dll"]
