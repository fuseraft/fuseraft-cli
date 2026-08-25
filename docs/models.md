# Models & Providers

## Specifying a model

The `Model` field on any agent (or on `Selection.Model`, `Selection.Magentic.Model`, `Compaction.Model`) accepts three forms:

### 1. Plain string — auto-detection

```yaml
Model: gpt-4o
```

The provider, API endpoint, and API key environment variable are inferred from the model ID prefix. Nothing else needs to be configured.

### 2. Named alias — registry reference

Define aliases once in the top-level `Models` dictionary, then reference by name:

```yaml
Models:
  fast:
    ModelId: grok-4.3
    ReasoningEffort: none
  smart:
    ModelId: grok-4.3
    ReasoningEffort: low

Agents:
  - Name: Planner
    Model:
      ModelId: fast
      MaxTokens: 4096
  - Name: Developer
    Model:
      ModelId: fast
      MaxTokens: 16384
  - Name: Tester
    Model:
      ModelId: smart
      MaxTokens: 8192
```

Per-agent `Temperature` and `MaxTokens` override the alias values.

### 3. Inline object — full manual control

```yaml
Model:
  ModelId: my-model
  Provider: openai
  Endpoint: https://my-proxy.example.com/v1
  ApiKeyEnvVar: MY_PROXY_KEY
  MaxTokens: 8192
  Temperature: 0.2
```

Any field left empty falls back to auto-detection.

---

## ModelConfig fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ModelId` | string | — | Model identifier sent to the API. |
| `Provider` | string | auto | Connector type: `openai`, `azure`, `google`, `mistral`, `ollama`. Auto-detected from `ModelId` if omitted. |
| `Endpoint` | string | auto | API base URL. Auto-detected from provider if omitted. Required for `azure`. Falls back to `endpoint` in `~/.fuseraft/config` when blank. |
| `ApiKeyEnvVar` | string | auto | Name of the environment variable holding the primary API key. Auto-detected from provider if omitted. Leave empty for `ollama`. Falls back to `apiKeyEnvVar` in `~/.fuseraft/config` when blank. |
| `ApiKey` | string | — | Literal API key. Takes precedence over `ApiKeyEnvVar`. Used by the REPL wizard; not recommended in YAML configs. |
| `ApiKeys` | array | — | Additional literal API keys for pool rotation. See [Credential pool rotation](#credential-pool-rotation). |
| `ApiKeyEnvVars` | array | — | Additional environment variable names each holding an API key, for pool rotation. See [Credential pool rotation](#credential-pool-rotation). |
| `MaxTokens` | int | `0` | Max tokens per response. `0` = use model default. |
| `MaxContextTokens` | int | `0` | Input context window limit (≈85% of the model's advertised maximum). Requests that would exceed this value are rejected before the API call — prevents expensive failures on models with hard limits. `0` disables the check. |
| `MaxPayloadBytes` | integer | `0` | Maximum serialized request body size in bytes. When set, the agent middleware estimates the outgoing JSON payload size (content × 1.2 + tool schemas × 1.1 + 2 KB envelope) before each API call and rejects it if it would exceed this limit — preventing HTTP 413 errors from upstream proxies (e.g. nginx). Set to your proxy's `client_max_body_size` minus ~10% headroom. `0` = no limit enforced. |
| `Temperature` | number | — | Sampling temperature (0.0–2.0). Omit for reasoning models that reject this parameter. |
| `ReasoningEffort` | string | — | Reasoning depth for models that support it (e.g. `grok-4.3`). Passed through verbatim — not validated against a fixed list, since accepted values are provider- and model-specific and keep growing (common: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`). Injected as `"reasoning": {"effort": "..."}` in the request. Omit for models that do not support this parameter. |
| `FalloverModels` | array | — | Ordered list of fallover models to try when this model fails with a classifiable error. Each entry supports the same shorthand as `ModelId` (a plain string in YAML). See [Fallover chain](#fallover-chain). |
| `FalloverOn` | array | — | Error reasons that trigger fallover. Defaults to all recoverable reasons: `RateLimit`, `ContextExceeded`, `QuotaExceeded`, `ServerError`. `AuthError` is never fallover-able. Only relevant when `FalloverModels` is set. |

---

## Auto-detection table

When `Endpoint` and `ApiKeyEnvVar` are not specified, they are filled in based on the model ID prefix:

| Model prefix | Provider | Default endpoint | API key env var |
|-------------|----------|-----------------|-----------------|
| `gpt-*` | openai | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| `o1*`, `o3*`, `o4*` | openai | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| `grok-*` | openai | `https://api.x.ai/v1` | `XAI_API_KEY` |
| `claude-*` | openai | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| `gemini-*`, `learnlm-*` | google | `https://generativelanguage.googleapis.com/v1beta/openai` | `GOOGLE_AI_API_KEY` |
| `mistral-*`, `mixtral-*` | mistral | `https://api.mistral.ai/v1` | `MISTRAL_API_KEY` |
| `codestral-*`, `pixtral-*` | mistral | `https://api.mistral.ai/v1` | `MISTRAL_API_KEY` |
| `deepseek-*` | openai | `https://api.deepseek.com/v1` | `DEEPSEEK_API_KEY` |
| `llama*`, `phi*`, `qwen*`, `gemma*`, `codellama*`, `smollm*` | ollama | `http://localhost:11434` | *(none)* |
| `name:tag` (colon format) | ollama | `http://localhost:11434` | *(none)* |

For any model not matching the table, specify `Provider`, `Endpoint`, and `ApiKeyEnvVar` explicitly.

---

## Global config defaults

`~/.fuseraft/config` can define a default `endpoint` and `apiKeyEnvVar` that are applied to every agent model (and named alias) that doesn't set those fields itself. This means you only need to configure the provider once — generated agent files work out of the box without repeating the values.

```json
{
  "modelId": "anthropic.claude-sonnet-4-6-20250929-v1:0",
  "endpoint": "http://localhost:3000/api/openai/v1",
  "apiKeyEnvVar": "OPENWEBUI_API_KEY",
  "replContextBudget": 400000
}
```

Set this file via `fuseraft repl` or `fuseraft models` (the setup wizard runs automatically on first use) or edit it directly. Run `fuseraft models` to see all models available from the configured provider, or use `/models` inside a REPL session for the same list.

### `replContextBudget` — REPL working-context override

The REPL trims conversation history against a working-context-token budget (`ctx.ContextTokenBudget`, shown in `/context`), separate from `MaxContextTokens` above. By default this budget comes from a per-model-family heuristic (150K for 1M/128K+-class frontier models like `claude-*`/`gemini-*`/`grok-*`/`gpt-5*`, 100K for ~128K-class models like `gpt-4*`/`mistral-*`/`deepseek-*`, 80K otherwise) — deliberately conservative, since the REPL's char-based token estimate doesn't account for tool-schema tokens.

Set `replContextBudget` in `~/.fuseraft/config` (a positive integer, in tokens) to override that heuristic for every model used in the REPL session, regardless of family. Leave it unset (or `0`) to keep the built-in heuristic. This is REPL-only and does not affect `MaxContextTokens` above (a separate per-agent hard ceiling enforced before each API call in non-REPL agent/orchestration contexts), nor the unrelated `ContextBudget` YAML block used in `orchestration.yaml` (warn/cutover/tool-result trimming for multi-agent orchestration runs) — the similarly-named `replContextBudget` field intentionally carries the `Repl` prefix to keep the two apart.

### OS keychain fallback

If an agent model has neither `ApiKey` nor `ApiKeyEnvVar` set after global defaults are applied, fuseraft retrieves the key stored in the OS keychain (set via `fuseraft key set` or the REPL wizard) and injects it as a literal `ApiKey`. This means the full auth resolution order for any agent model is:

1. Explicit `ApiKey` in the agent file (literal value)
2. `ApiKeyEnvVar` from the agent file (env var lookup)
3. `apiKeyEnvVar` from `~/.fuseraft/config` (env var lookup)
4. OS keychain (retrieved once at startup, injected as literal key)
5. Nothing — Ollama and other unauthenticated providers work without a key

Per-agent values always win; global values only fill in empty fields.

---

## Supported providers

### openai — OpenAI and OpenAI-compatible APIs

Uses `Microsoft.Extensions.AI` with the OpenAI connector. Works with any API that follows the OpenAI chat completions format.

Compatible services include: OpenAI, xAI (Grok), Anthropic (via their OpenAI-compatible endpoint), DeepSeek, OpenRouter, Groq, Together AI, LM Studio, vLLM, and many others.

```bash
export OPENAI_API_KEY=sk-...
```

```yaml
Model: gpt-4o
```

### azure — Azure OpenAI Service

Uses `Microsoft.Extensions.AI` with the Azure OpenAI connector. Requires `Endpoint` (your Azure resource URL) and `ApiKeyEnvVar`.

```bash
export AZURE_OPENAI_API_KEY=...
```

```yaml
Model:
  ModelId: gpt-4o
  Provider: azure
  Endpoint: https://my-resource.openai.azure.com/
  ApiKeyEnvVar: AZURE_OPENAI_API_KEY
```

`ModelId` maps to the Azure deployment name, not the underlying model name.

### google — Google AI Gemini

Uses `Microsoft.Extensions.AI` with the Google connector. Connects via the Google AI API.

```bash
export GOOGLE_AI_API_KEY=...
```

```yaml
Model: gemini-2.0-flash
```

### mistral — Mistral AI

Uses `Microsoft.Extensions.AI` with the Mistral connector.

```bash
export MISTRAL_API_KEY=...
```

```yaml
Model: mistral-large-latest
```

### ollama — Local models via Ollama

Uses `OllamaApiClient` from [OllamaSharp](https://github.com/awaescher/OllamaSharp). No API key required. The default endpoint is `http://localhost:11434`.

```yaml
Model: llama3.2
```

To use a custom Ollama endpoint:

```yaml
Model:
  ModelId: llama3.2
  Endpoint: http://192.168.1.50:11434
```

---

## Using Open WebUI

[Open WebUI](https://github.com/open-webui/open-webui) exposes an OpenAI-compatible API. Use the `openai` provider with your Open WebUI instance URL.

```bash
export OPENWEBUI_API_KEY=<key-from-owui-settings>
```

```yaml
Models:
  local-llama:
    ModelId: llama3.2
    Provider: openai
    Endpoint: http://localhost:3000/api/openai/v1
    ApiKeyEnvVar: OPENWEBUI_API_KEY
```

Generate the API key in Open WebUI under **Settings → Account → API Keys**.

---

## Mixing providers across agents

Each agent gets its own chat client and its own model. You can freely mix providers in a single config:

```yaml
Models:
  planner-model:
    ModelId: gpt-4o
  coder-model:
    ModelId: claude-3-5-sonnet-20241022
  local-reviewer:
    ModelId: llama3.2

Agents:
  - Name: Planner
    Model:
      ModelId: planner-model
    ...
  - Name: Developer
    Model:
      ModelId: coder-model
    ...
  - Name: Reviewer
    Model:
      ModelId: local-reviewer
    ...
```

Each agent's API calls are made with its own key and endpoint. Token costs are tracked and summed across all agents.

---

---

## Credential pool rotation

When multiple API keys are available for the same provider, fuseraft-cli automatically rotates between them on 429 Too Many Requests responses. This keeps long sessions alive when a single API key hits its rate limit.

### How it works

1. All keys from `ApiKey`, `ApiKeyEnvVar`, `ApiKeys`, and `ApiKeyEnvVars` are collected and deduplicated at session start.
2. Requests use the current slot (round-robin starting at 0).
3. When a 429 is returned — after [`TransientRetryHandler`](https://github.com/fuseraft/fuseraft-cli) exhausts its own per-request retries — the slot is marked with a 60-second cooldown and the next available slot is tried.
4. If all slots are simultaneously rate-limited, the session surfaces the error rather than busy-waiting.
5. Single-key configs (the common case) are unaffected: `KeyPoolChatClient` is only activated when more than one distinct key resolves.

### Configuration

```yaml
Model:
  ModelId: gpt-4o
  ApiKeyEnvVar: OPENAI_API_KEY_1       # primary key (env var)
  ApiKeyEnvVars:
    - OPENAI_API_KEY_2                 # rotated to on 429
    - OPENAI_API_KEY_3

# Or mix literal keys:
Model:
  ModelId: claude-sonnet-4-6
  ApiKeyEnvVar: ANTHROPIC_API_KEY
  ApiKeys:
    - sk-ant-...second-key...
    - sk-ant-...third-key...
```

Keys can also be sourced entirely from env vars:

```yaml
Model:
  ModelId: gpt-4o
  ApiKeyEnvVars:
    - OPENAI_KEY_TEAM_1
    - OPENAI_KEY_TEAM_2
    - OPENAI_KEY_TEAM_3
```

### Named alias with a pool

```yaml
Models:
  gpt4-pool:
    ModelId: gpt-4o
    ApiKeyEnvVar: OPENAI_API_KEY_1
    ApiKeyEnvVars:
      - OPENAI_API_KEY_2
      - OPENAI_API_KEY_3

Agents:
  - Name: Developer
    Model: gpt4-pool
```

All agents that reference the same alias share the same `KeyPoolChatClient` instance, so rotation state is shared across agents. Cooldowns from one agent's 429 apply to all agents using that pool for the duration of the cooldown window.

---

## Fallover chain

When a provider call fails with a classifiable error (rate limit, context exceeded, quota exhausted, or server error), fuseraft-cli can automatically retry on a different model. Configure an ordered `FalloverModels` list on any `ModelConfig` and the primary model is tried first; on failure the next entry is tried, and so on until one succeeds or all are exhausted.

```yaml
Model:
  ModelId: claude-opus-4-7
  FalloverModels:
    - gpt-4o           # tried if Anthropic returns 429 or 5xx
    - gemini-2.0-flash # final fallback
```

Each fallover entry supports the same shorthand as `ModelId` (a plain string) and goes through the full model resolution pipeline — including its own key pool if you configure `ApiKeys` or `ApiKeyEnvVars` on the fallover model.

### How it works

1. The primary model is tried first.
2. If it throws an exception whose cause is in `FalloverOn`, the error is logged to stderr and the next model in the chain is tried.
3. For streaming responses, fallover only fires before the first chunk is delivered — mid-stream exceptions propagate as-is (the caller has already received partial output).
4. If all models in the chain fail, the last exception is re-thrown.

### Fallover reasons

| Reason | HTTP status | Trigger condition |
|--------|-------------|-------------------|
| `RateLimit` | 429 | Request-rate limit hit (not billing-related). |
| `ContextExceeded` | 400 | Prompt exceeded the model's context window. |
| `QuotaExceeded` | 429 + quota/billing message | Account-level quota or billing limit reached. |
| `ServerError` | 5xx | Provider-side server error after all per-request retries. |
| `AuthError` | 401 / 403 | Invalid or missing credentials — **not** fallover-able by default. |

The default `FalloverOn` value covers all four recoverable reasons. Override it to restrict fallover to specific conditions:

```yaml
Model:
  ModelId: gpt-4o
  FalloverModels:
    - gemini-2.0-flash
  FalloverOn:
    - ContextExceeded   # only fallover when the prompt is too long
```

### Combining with credential pool rotation

`FalloverModels` and [credential pool rotation](#credential-pool-rotation) work independently and compose well. The key pool rotates between API keys for the **same model**; the fallover chain switches to a **different model** when all keys on the primary are exhausted:

```yaml
Model:
  ModelId: claude-opus-4-7
  ApiKeyEnvVar: ANTHROPIC_KEY_1
  ApiKeyEnvVars:
    - ANTHROPIC_KEY_2     # rotated to on 429
  FalloverModels:
    - gpt-4o              # tried after all Anthropic keys are rate-limited
```

### Named alias with a fallover chain

```yaml
Models:
  robust:
    ModelId: claude-opus-4-7
    FalloverModels:
      - gpt-4o
      - gemini-2.0-flash

Agents:
  - Name: Developer
    Model: robust
```

---

## Reasoning models

Reasoning models (OpenAI `o1`/`o3`/`o4`, xAI `grok-4.3`) reject the `temperature` parameter. Leave `Temperature` unset (null) for these models.

### xAI reasoning effort

`grok-4.3` supports four reasoning depth levels controlled by the `ReasoningEffort` field:

| Value | Behaviour |
|-------|-----------|
| `none` | Reasoning disabled — fastest, cheapest. Use for structured output, routing, and summarisation agents. |
| `low` | Light reasoning (default when unset on `grok-4.3`). Balances speed and analytical depth. |
| `medium` | More thinking tokens. Good for complex analysis, planning, and code review. |
| `high` | Maximum reasoning — slowest and most expensive. Reserve for the hardest problems. |

```yaml
Models:
  fast:
    ModelId: grok-4.3
    ApiKeyEnvVar: XAI_API_KEY
    ReasoningEffort: none      # structured output, routing agents

  reasoning:
    ModelId: grok-4.3
    ApiKeyEnvVar: XAI_API_KEY
    ReasoningEffort: low       # general agentic work

  deep:
    ModelId: grok-4.3
    ApiKeyEnvVar: XAI_API_KEY
    ReasoningEffort: high      # complex planning or review
```

The value is injected at the HTTP layer as `"reasoning": {"effort": "..."}` — no SDK-level support is required.

For OpenAI `o1`/`o3`/`o4`, leave `ReasoningEffort` unset; those models use a separate SDK-native mechanism (`ReasoningEffortLevel`) that the OpenAI SDK applies automatically.
