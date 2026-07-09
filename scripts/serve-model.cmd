@echo off
setlocal

rem Run vLLM directly via "docker run" (outside docker compose).
rem
rem Use this when you want the model server standalone -- e.g. on a dedicated GPU
rem box, or to iterate on vLLM flags without touching the compose stack.
rem
rem Blackwell / RTX 5090 (sm_120) caveats:
rem   - Needs a CUDA 12.8+ runtime image. Pre-built vllm/vllm-openai images may
rem     NOT ship sm_120 kernels and can fail to start on a 5090; a from-source
rem     vLLM build may be required. Override VLLM_IMAGE to point at such a build.
rem   - VLLM_FLASH_ATTN_VERSION=2 is passed below (FA2 path).
rem   - For a 32GB card, prefer a quantized Qwen that fits in memory by setting
rem     VLLM_MODEL accordingly.
rem
rem Reads from the environment (with defaults). Set any VLLM_* / HUGGING_FACE_HUB_TOKEN
rem in the shell first to override.

cd /d "%~dp0.."

if not defined VLLM_IMAGE set "VLLM_IMAGE=vllm/vllm-openai:latest"
if not defined VLLM_MODEL set "VLLM_MODEL=Qwen/Qwen2.5-7B-Instruct"
if not defined VLLM_PORT set "VLLM_PORT=8000"
if not defined VLLM_MAX_MODEL_LEN set "VLLM_MAX_MODEL_LEN=32768"
if not defined HUGGING_FACE_HUB_TOKEN set "HUGGING_FACE_HUB_TOKEN="

echo Serving %VLLM_MODEL% on port %VLLM_PORT% using image %VLLM_IMAGE%...

docker run --rm ^
  --gpus all ^
  --ipc=host ^
  -p %VLLM_PORT%:8000 ^
  -v hfcache:/root/.cache/huggingface ^
  -e HUGGING_FACE_HUB_TOKEN=%HUGGING_FACE_HUB_TOKEN% ^
  -e VLLM_FLASH_ATTN_VERSION=2 ^
  %VLLM_IMAGE% ^
  --model %VLLM_MODEL% ^
  --served-model-name %VLLM_MODEL% ^
  --enable-auto-tool-choice ^
  --tool-call-parser hermes ^
  --max-model-len %VLLM_MAX_MODEL_LEN%
