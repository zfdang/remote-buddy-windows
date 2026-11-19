#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

echo "=== Computing Deterministic Version ==="
eval "$(scripts/release-metadata.sh)"
echo "RELEASE_TAG=$RELEASE_TAG"
echo "RELEASE_PACKAGE_VERSION=$RELEASE_PACKAGE_VERSION"

echo "=== Restoring Solution ==="
dotnet restore RemoteBuddy.sln

echo "=== Building Solution ==="
dotnet build RemoteBuddy.sln -c Release -p:Version="$RELEASE_PACKAGE_VERSION"

echo "=== Running Unit Tests ==="
dotnet test RemoteBuddy.sln -c Release --no-build --verbosity normal

echo "=== Build Succeeded! ==="
