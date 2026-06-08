# Local-dev scripts

Helpers for the AgentMemoryOS local-dev infrastructure. They wrap the
`docker-compose.yml` at the repo root and read configuration from `.env`
(copy `.env.example` to `.env` first, or let `start.sh` do it for you).

## Default path vs `--gpu` path

- **Default (`./scripts/start.sh`)** starts only **postgres** (pgvector) and
  **redis**. This is the in-memory dev path (`MEMORY_STORE=inmemory`) and needs
  no GPU.
- **`--gpu` (`./scripts/start.sh --gpu`)** additionally starts the **vLLM**
  OpenAI-compatible model server (the compose `gpu` profile). The first run
  downloads and loads the model, which can take several minutes.

## Scripts

- **`start.sh`** — Brings the stack up with `docker compose up -d`. Creates
  `.env` from `.env.example` if missing, then polls health until postgres
  (`pg_isready`), redis (`redis-cli ping` → `PONG`), and — with `--gpu` — the
  vLLM `/v1/models` endpoint are ready. Prints a summary of endpoints.
  - `./scripts/start.sh` — postgres + redis only.
  - `./scripts/start.sh --gpu` — also start the vLLM model server.
- **`stop.sh`** — `docker compose down`. Preserves named volumes by default.
  - `./scripts/stop.sh` — stop containers, keep data.
  - `./scripts/stop.sh --volumes` — stop and remove volumes (pgdata, redisdata, hfcache).
- **`serve-model.sh`** — Runs vLLM standalone via `docker run` (outside compose)
  for when you want the model server on its own. Reads `VLLM_*` and
  `HUGGING_FACE_HUB_TOKEN` from the environment with sensible defaults.

## Blackwell / RTX 5090 (sm_120) caveat

Pre-built `vllm/vllm-openai` images may not include `sm_120` kernels and can
fail to start on an RTX 5090. If that happens, build/obtain a from-source vLLM
image (CUDA 12.8+) and override `VLLM_IMAGE` in `.env`. `VLLM_FLASH_ATTN_VERSION=2`
is already set. For a 32GB card, prefer a quantized Qwen model that fits in
memory by setting `VLLM_MODEL` accordingly.
