---
title: LLM providers
parent: Guides
nav_order: 5
---

# LLM providers

Without keys, Anthropic and OpenAI need a user connection (Connections) or an allowed system key. A new project gets the configured `Llm:DefaultProvider` when it is enabled and usable by its creator, else the first usable provider in the order Anthropic, OpenAI, Ollama, Fake (SPEC §4.24). Providers can be turned off per deployment with `Llm__Providers__<Name>__Enabled=false`; Railway turns off Ollama and Fake (FR-157).

| Setting | How |
| --- | --- |
| Anthropic | `Llm__Providers__Anthropic__ApiKey` |
| OpenAI | `Llm__Providers__OpenAI__ApiKey` |
| Ollama | `Llm__Providers__Ollama__BaseUrl` (no key). From the cluster it defaults to `http://host.docker.internal:11434` |
| Default provider | `Llm__DefaultProvider` (`anthropic`, `openai`, `ollama`, `fake`) |
| Per project | Settings, LLM tab: provider, model per agent and an optional per-project API key (encrypted, used only for that provider). Base URLs are system-only |

Default model IDs (from `appsettings.json`):

| Provider | Default model | Idea refinement |
| --- | --- | --- |
| Anthropic | `claude-opus-5-5` | `claude-sonnet-5-5` |
| OpenAI | `gpt-6-astra` | `gpt-6.1-sol` |
| Ollama | `qwen2.5-coder` | same as default |
| Fake | `fake-1` | same as default |

Override per agent with `Llm__Providers__<Name>__Models__<Agent>`. Agent names: `IdeaRefinement` and the other `AgentKind` values (names are case-insensitive). Costs come from `Llm:Pricing:<model-id>`. Add an entry for any unpriced model, for example `Llm__Pricing__gpt-6-astra__InputPerMTok`. A project token budget caps total tokens.
