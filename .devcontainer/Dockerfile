# Dev container for GTA Network.
#
# Everything except the C++/CLI ScriptHookVDotNet builds here: the .NET 10 server, launcher, bot and
# the in-game client (GTANetwork.dll, net48) all compile on Linux thanks to the managed SHVDN
# reference stub and Microsoft.NETFramework.ReferenceAssemblies. The real ScriptHookVDotNet still
# needs Windows + MSVC and is built only by the Windows CI job.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG USERNAME=dev
ARG USER_UID=1000
ARG USER_GID=${USER_UID}

# Tools used by the build/test/package scripts (python3: eng/integration-test-auth.sh edits settings.xml with
# it, as on the CI runner). git is usually already in the SDK image; installing
# it again is a no-op.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      git zip unzip rsync curl ca-certificates procps less python3 \
 && rm -rf /var/lib/apt/lists/*

# A non-root user. VS Code remaps its UID/GID to yours (updateRemoteUserUID, on by default) so files
# written into bind-mounted folders — the repo and, if you mount it, your install — stay owned by you.
# The sdk:10.0 image (Ubuntu 24.04) already has a user "ubuntu" with UID/GID 1000: reuse an existing UID/GID by
# renaming that account instead of failing in useradd.
RUN set -e; \
    if getent group "${USER_GID}" >/dev/null; then g="$(getent group "${USER_GID}" | cut -d: -f1)"; [ "$g" = "${USERNAME}" ] || groupmod -n "${USERNAME}" "$g"; else groupadd --gid "${USER_GID}" "${USERNAME}"; fi; \
    if getent passwd "${USER_UID}" >/dev/null; then u="$(getent passwd "${USER_UID}" | cut -d: -f1)"; [ "$u" = "${USERNAME}" ] || usermod -l "${USERNAME}" -d "/home/${USERNAME}" -m "$u"; else useradd --uid "${USER_UID}" --gid "${USER_GID}" -m "${USERNAME}"; fi; \
    mkdir -p "/home/${USERNAME}/.nuget/packages"; \
    chown -R "${USER_UID}:${USER_GID}" "/home/${USERNAME}"

# PowerShell for eng/package-client.ps1 (only needed to assemble a full release; the fast build+sync
# loop does not use it). Best-effort so the image still builds offline.
RUN . /etc/os-release \
 && curl -fsSL "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/pmc.deb \
 && dpkg -i /tmp/pmc.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends powershell \
 && rm -f /tmp/pmc.deb \
 && rm -rf /var/lib/apt/lists/* \
 || echo "PowerShell not installed (offline build?); eng/package-client.ps1 will be unavailable."

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    NUGET_XMLDOC_MODE=skip \
    GTAN_INSTALL=/gtanetwork

USER ${USERNAME}
WORKDIR /workspace
