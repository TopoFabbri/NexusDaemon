# NexusDaemon — Stateless AI Orchestrator & Project Manager

`NexusDaemon` is a .NET 8 Worker Service that acts as a **stateless API router** driven by a central `StateManager` and a bounded `SlidingWindowQueue`.

---

## Architecture (Phases 1–5)

1. **Phase 1 — The Chat Interface (`TelegramChatGateway`)**:
   - Long-polls the Telegram Bot API (`AllowedChatId: 6622366604`), handles built-in director commands (`/status`, `/model <id>`, `/models`, `/projects`, `/clear`), and forwards raw text commands to the stateless Nexus router.
2. **Phase 2 — State & Memory Engine (`SlidingWindowQueue` & `StateManager`)**:
   - `SlidingWindowQueue`: Thread-safe bounded queue constrained to the last 5–10 interactions while preserving Gemini's `user` -> `model` role-alternation rule.
   - `StateManager`: Tracks active Antigravity workers (`ActiveWorkers`), active headless builds (`ActiveBuilds`), pending HITL approvals (`PendingApproval`), active router/worker models (default: `hydrogen-4`), and live Git status across registered repositories (`HordePunkMaidens`, `Quantum-Quartz`).
3. **Phase 3 — Stateless Nexus Router (`NexusRouterService`)**:
   - Holds no persistent chat session. Every turn assembles a fresh prompt from scratch (`HardcodedSystemPrompt` + live `StateManager` snapshot + `SlidingWindowQueue` history), sends a single stateless request to the Gemini API, and closes the connection immediately.
4. **Phase 4 — Toolset & Event Bus (`NexusToolDispatcher`, `NexusEventBus`, `AntigravityDaemonRunner`, `CliStreamJsonRunner`, `BuildRunner`)**:
   - `DelegateTask` and `RunLocalBuild` register active tasks in `StateManager` and run asynchronously in the background so Nexus sleeps immediately.
   - When a worker or build finishes, `AgentTaskCompletedEvent` / `BuildCompletedEvent` is published to `NexusEventBus`, triggering a new stateless request to summarize the outcome for Telegram.
5. **Phase 5 — Long-Term Memory & Guardrails (`ProjectFileReader` & `HitlApprovalInterceptor`)**:
   - `ReadFile`: Dynamically reads `Architecture.md`, `Lore.md`, or source files from the sandboxed project root into the ephemeral prompt context on demand.
   - `HitlApprovalInterceptor`: Intercepts tool calls with `isDestructive: true`, holding execution in `StateManager.PendingApproval` until the user replies `Y` or `N` on Telegram.

---

## Quick Start

1. **Set Environment Variables** (or configure `src/NexusDaemon/appsettings.json`):
   ```powershell
   $env:GEMINI_API_KEY = "your-google-ai-studio-api-key"
   $env:TELEGRAM_BOT_TOKEN = "your-telegram-bot-token"
   ```
2. **Build & Test**:
   ```powershell
   & "C:\Program Files\dotnet\dotnet.exe" build D:\Files\Repos\NexusDaemon\NexusDaemon.sln -c Release
   & "C:\Program Files\dotnet\dotnet.exe" test D:\Files\Repos\NexusDaemon\NexusDaemon.sln -c Release
   ```
3. **Run the Daemon**:
   ```powershell
   & "C:\Program Files\dotnet\dotnet.exe" run --project D:\Files\Repos\NexusDaemon\src\NexusDaemon\NexusDaemon.csproj -c Release
   ```
