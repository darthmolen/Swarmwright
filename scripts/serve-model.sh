#!/usr/bin/env bash
set -euo pipefail

# Run vLLM directly via `docker run` (outside docker compose).
#
# Use this when you want the model server standalone -- e.g. on a dedicated GPU
# box, or to iterate on vLLM flags without touching the compose stack.
#
# Blackwell / RTX 5090 (sm_120) caveats:
#   - Needs a CUDA 12.8+ runtime image. Pre-built vllm/vllm-openai images may
#     NOT ship sm_120 kernels and can fail to start on a 5090; a from-source
#     vLLM build may be required. Override VLLM_IMAGE to point at such a build.
#   - VLLM_FLASH_ATTN_VERSION=2 is exported below (FA2 path).
#   - For a 32GB card, prefer a quantized Qwen that fits in memory (e.g. an
#     AWQ/GPTQ build) by setting VLLM_MODEL accordingly.
#
# Reads from the environment (with defaults); source your .env first if desired:
#   set -a; source .env; set +a; ./scripts/serve-model.sh

cd "$(dirname "$0")/.."

VLLM_IMAGE="${VLLM_IMAGE:-vllm/vllm-openai:latest}"
VLLM_MODEL="${VLLM_MODEL:-Qwen/Qwen2.5-7B-Instruct}"
VLLM_PORT="${VLLM_PORT:-8000}"
VLLM_MAX_MODEL_LEN="${VLLM_MAX_MODEL_LEN:-32768}"
HUGGING_FACE_HUB_TOKEN="${HUGGING_FACE_HUB_TOKEN:-}"

echo "Serving ${VLLM_MODEL} on port ${VLLM_PORT} using image ${VLLM_IMAGE}..."

docker run --rm \
  --gpus all \
  --ipc=host \
  -p "${VLLM_PORT}:8000" \
  -v hfcache:/root/.cache/huggingface \
  -e HUGGING_FACE_HUB_TOKEN="${HUGGING_FACE_HUB_TOKEN}" \
  -e VLLM_FLASH_ATTN_VERSION=2 \
  "${VLLM_IMAGE}" \
  --model "${VLLM_MODEL}" \
  --served-model-name "${VLLM_MODEL}" \
  --enable-auto-tool-choice \
  --tool-call-parser hermes \
  --max-model-len "${VLLM_MAX_MODEL_LEN}"
