# SAG(Structured Agent Graph) IDE

Local-first deterministic workflow + RAG runtime for agent-native engineering.

Built as a .NET 9 orchestration service with SQLite WAL persistence, prompt registry/templates, scheduler, and RAG pipeline (fetch → chunk → embed → vector search) plus a thin VS Code extension. Runs fully local with Ollama; swap to Claude/Codex/Gemini by adding keys.

## What makes this different
- Local-first, provider-agnostic: works fully offline with Ollama; affinity routing spans local/cloud (Claude/Codex/Gemini) and multiple Ollama hosts.
- Workflow + RAG: queueing/scheduling, DLQ, retries/timeouts, prompt registry/templates, and a built-in RAG pipeline (web fetch/search, chunk, embed, vector search) that feeds orchestrated subtasks.
- Split architecture: orchestration engine in a .NET service; thin VS Code extension over named pipes (<10ms). Broadcasts reach all clients fast and don’t burden the extension host.
- Workflow engine: DAGs, routers, pause/resume, human approval gates, auditable activity logs—closer to a mini Temporal/Airflow than chat UIs.

## Capabilities
- Task orchestration with queueing, scheduler, DLQ, persistence, retry/timeout policies.
- Workflow engine with DAGs/routers/pause/resume/human approval gates, context var substitution ({{var}}), Git-linked activity logging, and prompt registry/templates loaded from `prompts/`.
- RAG pipeline: web fetch/search, text chunking, embeddings, vector search, cache, and safety redaction feeding orchestrated subtasks.
- Multi-provider LLM support (Claude, Codex, Gemini, Ollama) with streaming, token counting, structured parsing, and affinity-based model routing across multiple Ollama hosts.
- VS Code UI: Active Tasks, History, DLQ, Workflow Explorer graph, Streaming Output, Diff Approval, Comparison panel, Problems integration.
- Extras: CLI (`tools/cli/sag`), Logseq plugin scaffold, build/deploy helpers (`utils/*.ps1`/`.sh`), comparison groups, configurable concurrency.

## Architecture (high level)

### Task and workflow path
```mermaid
sequenceDiagram
  participant U as User
  participant V as VS Code Extension
  participant P as Named Pipe
  participant S as .NET Orchestration Service
  participant M as Model Provider
  participant D as Persistence (SQLite)

  U->>V: Submit Task / Run Workflow
  V->>P: PipeMessage (submit_task/start_workflow)
  P->>S: MessageHandler (15+ message types)
  S->>D: Persist task/workflow (SQLite WAL)
  S->>M: Send prompt (streaming enabled)
  M-->>S: Stream output (real-time chunks, token counts)
  S-->>P: Task updates / streaming (<0.5ms broadcast)
  P-->>V: UI updates
  V-->>U: Status, output, diffs (Apply/Skip buttons)
```

### Why named pipes
- Keeps the extension host lean; orchestration/state lives in the isolated .NET process with bidirectional IPC and per-client write locks.
- Avoids Node event-loop stalls under heavy streaming with concurrent task queue, retry policies, and timeout management.
- Works cross-platform; service can be restarted independently of VS Code. Binary framing (4-byte length prefix) ensures message boundary integrity.
- ProviderFactory routes tasks to 4 HTTP providers (Claude, Codex, Gemini), Ollama, or TensorRT-LLM with affinity-based server selection.

## Updates (2026-02-25)
- Added full RAG pipeline and orchestration stack (workflow engine, prompt registry/templates, subtask coordinator, scheduler, RAG fetch/chunk/embed/vector store/search) with new API endpoints and resilience/plumbing updates.
- Refreshed clients and tooling: VS Code extension prompt library/commands, CLI entry point, Logseq plugin scaffolding, deployment/run scripts (Ollama/Searxng), and config adjustments for providers/models.
- Introduced comprehensive test suite and prompt assets (robotics, summarization, code review), plus new samples/build templates to validate agent routing, RAG flows, scheduler, providers, and endpoints.

## Updates (2026-02-21)
- Local-first stack steady: .NET 9 service, SQLite WAL persistence, named pipes <10ms, affinity routing across Claude/Codex/Gemini/Ollama/TensorRT-LLM.
- Workflow engine stable: DAGs/routers, pause/resume, approval gates with diffs, Git-linked activity logs survive editor restarts.
- Comparison + reliability: grouped multi-model runs with token-counted streaming and <0.5ms broadcasts; DLQ with retry/discard and backoff; 50-entry history cap; Active Tasks/History/DLQ/Workflow Explorer/Streaming Output/Diff Approval/Problems panels remain consistent.

## Updates (2026-02-20)
- Shipped: orchestration with DLQ/persistence (SQLite WAL), multi-provider streaming UI (real-time chunks with token counts), workflow engine (DAGs, routers, approval gates, pause/resume), Git-linked activity logging (markdown generation), comparison groups (all N models side-by-side), diagnostics (issues parsed to Problems panel).
- In progress: harden streaming reliability at high token rates (>500 tok/sec), expand workflow templates (security audit, API generation, code migration), improve DLQ UI (batch retry, error categorization).

## Quickstart

### Prerequisites
- VS Code 1.85+
- .NET SDK 9.0
- Node.js 20+ and npm 10+
- Optional: Ollama for local models and SearXNG for RAG web search

### 1) Clone
```bash
git clone https://github.com/sanjeevakumarh/SAGExtention.git
cd SAGExtention
```

### 2) Start the orchestration service
```bash
dotnet run --project src/SAGIDE.Service/SAGIDE.Service.csproj
```
Leave this running; it hosts named pipes, task orchestration with scheduler/queue/DLQ, SQLite WAL persistence (~50 task history limit), prompt registry/templates, RAG pipeline, and provider routing.

### 3) Start the VS Code extension
1. Open the repo in VS Code.
2. Open `src/vscode-extension`.
3. Install deps:
   ```bash
  npm ci
   ```
4. Press F5 to launch the Extension Development Host.

### 4) Optional: start CLI
```bash
dotnet run --project tools/cli/sag/sag.csproj -- --help
```

### 5) Optional: start Logseq plugin (dev)
- Install Logseq, enable dev plugins, and point to `tools/logseq-plugin` after running `npm install && npm run build` there.

### 6) Run a task or workflow
- In the Extension Host window, open a code file.
- Press Ctrl+Shift+P and run `SAG: Submit Task`.
- Choose an agent and model (local or paid).
- Watch Active Tasks, Streaming Output, and History panes.

## Model and RAG configuration
Configuration lives in two places:
- Service: `src/SAGIDE.Service/appsettings.json` (or appsettings.Template.json as a starter)
- Extension: `sagIDE.*` VS Code settings

### Local (Ollama)
1. Install Ollama: https://ollama.com (or TensorRT-LLM for edge devices like Orin Nano / Jetson)
2. Pull a model:
   ```bash
   ollama pull qwen2.5-coder:7b-instruct
   ```
3. Verify:
   ```bash
   ollama list
   ```
4. Verify via HTTP (service health and tags):
   ```bash
   curl http://localhost:11434/api/tags
   ```

Service example (trim to your hosts/models):
```json
{
  "SAGIDE": {
    "PromptsPath": "../../prompts",
    "NamedPipeName": "SAGIDEPipe",
    "MaxConcurrentAgents": 5,
    "Scheduler": { "Enabled": true },
    "Providers": { "Claude": { "MaxTokens": 4096 }, "Gemini": { "MaxTokens": 4096 }, "Codex": { "MaxTokens": 4096 }, "Ollama": { "MaxTokens": 4096 } },
    "Rag": { "EmbeddingBatchSize": 32, "ChunkSize": 1500, "ChunkOverlap": 200, "CacheTtlHours": 4, "RateLimitDelayMs": 1000 },
    "Ollama": {
      "Servers": [
        { "Name": "localhost", "BaseUrl": "http://localhost:11434", "RagOrder": 0, "SearchUrl": "http://localhost:8888", "Models": ["nomic-embed-text", "qwen2.5-coder:7b-instruct"] }
      ]
    },
    "OpenAICompatible": { "Servers": [] },
    "ApiKeys": { "Anthropic": "", "OpenAI": "", "Google": "" }
  }
}
```

### Paid providers
Add keys to `appsettings.json` under `SAGIDE:ApiKeys`:
```json
{
  "SAGIDE": {
    "ApiKeys": {
      "Anthropic": "YOUR_KEY",
      "OpenAI": "YOUR_KEY",
      "Google": "YOUR_KEY"
    }
  }
}
```
Then select the provider in `SAG: Submit Task`.


## Defining Workflows and Prompts (YAML)
- Prompts/templates live under `prompts/` and are loaded by the Prompt Registry.
- Workflows live in `.agentide/workflows/*.yaml` (or built-in templates); they support DAG dependencies, conditional routing, context vars, human approval gates, and convergence policies.

```yaml
name: "Refactor and Test"
description: "Refactors code with approval, then generates tests"
params:
  - name: file_path
    description: "Target file"
  - name: quality_target
    description: "refactor for readability or performance"
    default: "readability"
steps:
  - id: refactor
    type: agent
    agent: Refactoring
    modelId: claude-3-sonnet
    prompt: "Refactor {{file_path}} for {{quality_target}}. Output unified diffs."
    maxIterations: 1
    
  - id: wait_approval
    type: human_approval
    prompt: "Review refactoring diffs in {{refactor.output}}. Proceed with tests?"
    slaHours: 1
    depends_on: [refactor]
    
  - id: tests
    type: agent
    agent: TestGeneration
    depends_on: [wait_approval]
    modelId: gpt-4o-mini
    prompt: "Generate unit tests for the refactored code in {{refactor.output}}. Aim for >80% coverage."
    maxIterations: 1

convergencePolicy:
  maxIterations: 2
  escalationTarget: HUMAN_APPROVAL
  partialRetryScope: FAILING_NODES_ONLY
  timeoutPerIterationSec: 120
```


## Verification and FAQ

### Quick connectivity check
- `ollama list` shows at least one model (if using local).
- Service terminal shows `dotnet run` logs with NamedPipeServer listening on the configured pipe name (Windows: `\\.\pipe\AgenticIDEPipe` or Unix: `/tmp/AgenticIDEPipe`).
- VS Code status bar shows `$(check) SAG: Connected`; Output panel → `SAG IDE` shows heartbeat/connection logs.

### How do I run only local models?
- Install Ollama, pull a model: `ollama pull qwen2.5-coder:7b-instruct`.
- In `SAG: Submit Task`, select your Ollama model from the list (detected via ProviderFactory affinity routing).
- Leave `AgenticIDE:ApiKeys` section empty in `appsettings.json` (no cloud keys configured = no cloud access).

### How do I fix "Service not running"?
- Start the backend: `cd src/SAGIDE.Service && dotnet run` (will log NamedPipeServer startup and DI registration).
- Verify `sagIDE.pipeName` in VS Code settings matches `AgenticIDE:NamedPipeName` in `appsettings.json` (default: `AgenticIDEPipe`).
- Check firewall: Windows Defender may block named pipes on first run (allow when prompted).
- If extension shows "Disconnected" but service is running, extension auto-reconnects every 3 seconds (exponential backoff).

### Where do workflows live?
- Built-in workflows: shipped with the service (in memory, loaded by WorkflowEngine on startup).
- Custom workflows: `.agentide/workflows/*.yaml` in your workspace root (parsed by AgentOrchestrator during `start_workflow` message handling).
- Syntax validation: DAG topological sort, step type validation (agent/router/tool/constraint/human_approval), dependency resolution.

## Troubleshooting quick links
- Ollama install: https://ollama.com
- Service logs: `src/SAGIDE.Service/logs/` (Serilog, daily rolling files, Info+ level)
- Extension logs: Output panel → `SAG IDE` (connects, submits tasks, receives broadcasts)
- Named pipe (Windows): Resource Monitor → Handles, search for `AgenticIDEPipe` to verify listening
- Streaming stalls: Check agent output token rate; if <100 tok/sec, model may be overloaded or rate-limited
- DLQ inspection: `sagIDE.showDlq` command shows failed tasks with error codes and retry counts

## Roadmap (short)
- Harden streaming reliability at high token rates (>1000 tok/sec agents).
- Expand workflow templates (security audit, API generation, code migration templates).
- Improve DLQ UI (batch retry, error classification, escalation alerts).

