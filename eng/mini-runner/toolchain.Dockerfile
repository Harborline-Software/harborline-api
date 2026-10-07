# Official Linux ARM64 SDK 11 RC image; immutable manifest checked against MCR.
# The existing package-consumer executable targets net10.0. Copy its runtime only.
FROM mcr.microsoft.com/dotnet/sdk@sha256:e47b8fe8c16821354690e16ef744d53a53a9cdae13ad9e000058913cd0368718 AS consumer-runtime
FROM node@sha256:d6aa754f16b3197301076f047b5def2f02ea1dbbc2ca920407d46d7ec7f87b20 AS node
FROM rust@sha256:2c3a22f0a5533ea2dd5a16627bc841228151faa2d4de2644ac9987e4a2f1f2fa AS rust
FROM mcr.microsoft.com/dotnet/sdk@sha256:001dc5a48897722fe0c0550f95dca4d8eb4b5bb67eac9ea3723f36ba6d02d613
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
COPY --from=rust /usr/local/cargo /usr/local/cargo
COPY --from=rust /usr/local/rustup /usr/local/rustup
ENV RUSTUP_HOME=/usr/local/rustup PATH=/usr/local/cargo/bin:/usr/local/bin:/usr/local/sbin:/usr/sbin:/usr/bin:/sbin:/bin
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && npm install -g pnpm@11.1.3 \
 && apt-get update \
 && apt-get install -y --no-install-recommends git curl python3 jq time procps util-linux build-essential ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /opt/mini-toolchain \
 && dpkg-query -W > /opt/mini-toolchain/dpkg.tsv \
 && test "$(dotnet --version)" = '11.0.100-rc.1.26425.128' \
 && test "$(node --version)" = 'v24.21.0' \
 && test "$(pnpm --version)" = '11.1.3'
RUN useradd --uid 1001 --create-home runner && mkdir -p /work && chown 1001:1001 /work
USER 1001:1001
WORKDIR /work

COPY --from=consumer-runtime /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12
RUN test "$(dotnet --version)" = '11.0.100-rc.1.26425.128' \
 && dotnet --list-runtimes | grep -F 'Microsoft.NETCore.App 10.0.12 [/usr/share/dotnet/shared/Microsoft.NETCore.App]'
