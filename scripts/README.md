# Local-dev scripts

Helpers for the Swarmwright local-dev infrastructure. They wrap the
`docker-compose.yml` at the repo root and read configuration from `.env`
(copy `.env.example` to `.env` first, or let `start.sh` do it for you).

## Default path vs `--gpu` path

- **Default (`./scripts/start.sh`)** starts only **postgres** (optional — the
  app defaults to the InMemory provider). Needs no GPU.
- **`--gpu` (`./scripts/start.sh --gpu`)** additionally starts the **vLLM**
  OpenAI-compatible model server (the compose `gpu` profile). The first run
  downloads and loads the model, which can take several minutes.

## Scripts

- **`start.sh`** — Brings the stack up with `docker compose up -d`. Creates
  `.env` from `.env.example` if missing, then polls health until postgres
  (`pg_isready`) and — with `--gpu` — the vLLM `/v1/models` endpoint are ready.
  Prints a summary of endpoints.
  - `./scripts/start.sh` — postgres only.
  - `./scripts/start.sh --gpu` — also start the vLLM model server.
- **`stop.sh`** — `docker compose down`. Preserves named volumes by default.
  - `./scripts/stop.sh` — stop containers, keep data.
  - `./scripts/stop.sh --volumes` — stop and remove volumes (pgdata, hfcache).
- **`serve-model.sh`** — Runs vLLM standalone via `docker run` (outside compose)
  for when you want the model server on its own. Reads `VLLM_*` and
  `HUGGING_FACE_HUB_TOKEN` from the environment with sensible defaults.

## Windows (Docker Desktop)

`start.cmd`, `stop.cmd`, and `serve-model.cmd` are batch equivalents for running the
stack from a Windows terminal (PowerShell or cmd) without WSL — Docker Desktop puts
`docker` on the Windows PATH and forwards published container ports to Windows
`localhost` (postgres 5432, vLLM 8000). Same arguments as the shell scripts:

```bat
scripts\start.cmd            :: postgres only
scripts\start.cmd --gpu      :: also start vLLM
scripts\stop.cmd             :: stop, keep volumes
scripts\stop.cmd --volumes   :: stop and remove volumes
```

If you have a WSL distro with Docker Desktop integration, running the `.sh` scripts in
WSL works just as well — the ports still front through to Windows `localhost`.

## Configuring the example host (`set-user-secrets.ps1`)

The .NET app does **not** read `.env` — it uses `appsettings.json`, environment variables, and
`dotnet user-secrets`. To run `tests/Swarmwright.Example.WebHost` without an "AzureOpenAI
configuration section is missing" error, populate user-secrets from `.env`:

```powershell
# Azure OpenAI / Foundry path (default) — uses MAF_AIF_* from .env
pwsh ./scripts/set-user-secrets.ps1

# Local vLLM path — uses VLLM_* from .env (start the model first: scripts\start.cmd --gpu)
pwsh ./scripts/set-user-secrets.ps1 -Provider vllm
```

It writes `AzureOpenAI:Endpoint/ApiKey/DeploymentName` (azure) or `OpenAI:Endpoint/Model/ApiKey`
(vllm) into the host's per-user secret store (Development only; never committed). Then:

```powershell
dotnet run --project tests/Swarmwright.Example.WebHost   # browse https://localhost:7001
```

## Blackwell / RTX 5090 (sm_120) caveat

Pre-built `vllm/vllm-openai` images may not include `sm_120` kernels and can
fail to start on an RTX 5090. If that happens, build/obtain a from-source vLLM
image (CUDA 12.8+) and override `VLLM_IMAGE` in `.env`. `VLLM_FLASH_ATTN_VERSION=2`
is already set. For a 32GB card, prefer a quantized Qwen model that fits in
memory by setting `VLLM_MODEL` accordingly.
