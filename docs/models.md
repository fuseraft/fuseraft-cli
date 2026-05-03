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
    ModelId: grok-4-1-fast-non-reasoning
  smart:
    ModelId: grok-4-1-fast-reasoning

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
| `Endpoint` | string | auto | API base URL. Auto-detected from provider if omitted. Required for `azure`. |
| `ApiKeyEnvVar` | string | auto | Name of the environment variable holding the API key. Auto-detected from provider if omitted. Leave empty for `ollama`. |
| `MaxTokens` | int | `0` | Max tokens per response. `0` = use model default. |
| `Temperature` | number | — | Sampling temperature (0.0–2.0). Omit for reasoning models that reject this parameter. |

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

## Reasoning models

Reasoning models (OpenAI `o1`/`o3`/`o4`, xAI `grok-*-reasoning`) reject the `temperature` parameter. Leave `Temperature` unset (null) for these models:

```yaml
Model:
  ModelId: o3-mini
  MaxTokens: 8192
```

Non-reasoning models default to the provider's built-in temperature if `Temperature` is omitted.
