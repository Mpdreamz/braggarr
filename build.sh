#!/usr/bin/env bash
# ./build.sh [build|test|run]   (default: test)
set -euo pipefail
cd "$(dirname "$0")"
case "${1:-test}" in
  build) dotnet build braggarr.slnx ;;
  test)  dotnet build braggarr.slnx && dotnet test --solution braggarr.slnx --no-build ;;
  run)   dotnet run --project src/Braggarr.Cli -- serve ;;
  *) echo "usage: ./build.sh [build|test|run]" >&2; exit 2 ;;
esac
