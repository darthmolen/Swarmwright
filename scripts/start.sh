#!/usr/bin/env bash
set -euo pipefail

# Start the AgentMemoryOS local-dev stack.
#
# Usage:
#   ./scripts/start.sh          # postgres + redis only (in-memory dev path)
#   ./scripts/start.sh --gpu    # also start the vLLM model server (gpu profile)

cd "$(dirname "$0")/.."

GPU=false
if [[ "${1:-}" == "--gpu" ]]; then
  GPU=true
fi

# Ensure a .env exists so compose interpolation works.
if [[ ! -f .env ]]; then
  cp .env.example .env
  echo "No .env found; created one from .env.example. Review it before going further."
fi

# Load env so we can reference ports (e.g. VLLM_PORT) in this script.
set -a
# shellcheck disable=SC1091
source .env
set +a

# wait_for <description> <timeout-seconds> <command...>
# Polls the command until it succeeds or the timeout is reached.
wait_for() {
  local desc="$1"
  local timeout="$2"
  shift 2
  local elapsed=0
  printf 'Waiting for %s' "$desc"
  while ! "$@" >/dev/null 2>&1; do
    if (( elapsed >= timeout )); then
      printf '\n'
      echo "ERROR: timed out after ${timeout}s waiting for ${desc}." >&2
      echo "Check logs with: docker compose logs" >&2
      exit 1
    fi
    printf '.'
    sleep 5
    elapsed=$(( elapsed + 5 ))
  done
  printf ' ready.\n'
}

COMPOSE_ARGS=(up -d)
if [[ "$GPU" == true ]]; then
  COMPOSE_ARGS=(--profile gpu up -d)
fi

echo "Starting containers..."
docker compose "${COMPOSE_ARGS[@]}"

# Core services.
wait_for "postgres" 120 docker compose exec -T postgres pg_isready -U "${POSTGRES_USER}"

redis_ping() {
  [[ "$(docker compose exec -T redis redis-cli ping 2>/dev/null | tr -d '\r')" == "PONG" ]]
}
wait_for "redis" 120 redis_ping

if [[ "$GPU" == true ]]; then
  echo "Note: the first run downloads and loads the model; this can take several minutes."
  vllm_ready() {
    curl -fs "http://localhost:${VLLM_PORT:-8000}/v1/models" >/dev/null 2>&1
  }
  # Up to ~10 minutes for model download/load.
  wait_for "vLLM model server" 600 vllm_ready
fi

echo ""
echo "Stack is up. Endpoints:"
echo "  Postgres : ${POSTGRES_CONNECTION:-Host=localhost;Port=${POSTGRES_PORT:-5432}}"
echo "  Redis    : localhost:${REDIS_PORT:-6379}"
if [[ "$GPU" == true ]]; then
  echo "  vLLM     : http://localhost:${VLLM_PORT:-8000}/v1  (model: ${VLLM_MODEL:-?})"
else
  echo "  vLLM     : not started (run './scripts/start.sh --gpu' to enable)"
fi
