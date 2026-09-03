#!/usr/bin/env bash
# Prints the GTA Network version numbers derived from the commit date, so that every CI job
# (and every project inside a job) agrees on one version:
#   build    = days since 2016-01-01
#   revision = UTC minutes since midnight / 2
# Usage: eval "$(eng/version.sh)"   -> GTAN_BUILD, GTAN_REVISION, GTAN_VERSION
set -euo pipefail
ts=$(git log -1 --format=%ct 2>/dev/null || date -u +%s)
epoch2016=1451606400
build=$(( (ts - epoch2016) / 86400 ))
revision=$(( ((ts % 86400) / 60) / 2 ))
echo "GTAN_BUILD=$build"
echo "GTAN_REVISION=$revision"
echo "GTAN_VERSION=0.1.$build.$revision"
