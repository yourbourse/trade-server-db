#!/usr/bin/env bash
#
# Wrapper over the Migrator CLI.
#
#   ./migrate.sh --provider clickhouse --connection-string "Host=localhost;Port=8123;Database=trade;Username=default;Password=..."
#   ./migrate.sh --provider postgres   --connection-string "Host=localhost;Port=5432;Database=trade;Username=postgres;Password=..."
#   ./migrate.sh --export-config /opt/trade-server/config/export_config.json
#
# Every argument is passed straight through to the Migrator, so `--help` lists the full set of options.
#
# The Migrator runs from the root of the repository, so it picks up the scripts in ./clickhouse and ./psql as
# they are on disk - editing a .sql file and re-running is enough, no rebuild required.
#
# Environment:
#   TSDB_CONNECTION_STRING  used when --connection-string is not supplied
#   TSDB_NO_BUILD=1         skip the build and run the binary produced by a previous run
#   TSDB_CONFIGURATION      build configuration, defaults to Release

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$REPO_ROOT/src/Migrator/Migrator.csproj"
CONFIGURATION="${TSDB_CONFIGURATION:-Release}"
MIGRATOR_DLL="$REPO_ROOT/src/Migrator/bin/$CONFIGURATION/net10.0/Migrator.dll"

if [ ! -f "$PROJECT" ]; then
    echo "Migrator project not found: $PROJECT" >&2
    exit 1
fi

if [ "$#" -eq 0 ]; then
    echo "No arguments supplied. Run '$0 --help' to see the available options." >&2
    exit 1
fi

cd "$REPO_ROOT"

if [ "${TSDB_NO_BUILD:-0}" != "1" ]; then
    echo "------------------------------------"
    echo "Building the Migrator ($CONFIGURATION) ..."

    dotnet build "$PROJECT" -c "$CONFIGURATION" --nologo
fi

if [ ! -f "$MIGRATOR_DLL" ]; then
    echo "Migrator binary not found: $MIGRATOR_DLL" >&2
    exit 1
fi

echo "------------------------------------"
echo "Running the migration ..."

exec dotnet "$MIGRATOR_DLL" "$@"
