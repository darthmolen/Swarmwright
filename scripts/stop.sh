#!/usr/bin/env bash
set -euo pipefail

# Stop the Swarmwright local-dev stack.
#
# Usage:
#   ./scripts/stop.sh             # stop containers, keep data volumes
#   ./scripts/stop.sh --volumes   # stop containers AND remove named volumes

cd "$(dirname "$0")/.."

# Include the gpu profile so a running vLLM container is also stopped.
DOWN_ARGS=(--profile gpu down)
if [[ "${1:-}" == "--volumes" ]]; then
  DOWN_ARGS+=(--volumes)
  echo "Stopping stack and removing named volumes (pgdata, hfcache)..."
else
  echo "Stopping stack (volumes preserved)..."
fi

docker compose "${DOWN_ARGS[@]}"
echo "Done."
