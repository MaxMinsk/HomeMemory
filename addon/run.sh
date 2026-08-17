#!/usr/bin/with-contenv bashio
set -euo pipefail

export MEMORY_TRANSPORT="http"
export MEMORY_DB_PATH="$(bashio::config 'db_path')"
export ASPNETCORE_URLS="http://0.0.0.0:8099"
export Logging__LogLevel__Default="$(bashio::config 'log_level')"

if bashio::config.has_value 'bearer_token'; then
  export MEMORY_BEARER_TOKEN="$(bashio::config 'bearer_token')"
else
  bashio::log.fatal "No bearer_token set. The HTTP endpoint refuses to start unauthenticated — set 'bearer_token' in the add-on options."
  exit 1
fi

if bashio::config.has_value 'allowed_domains'; then
  export MEMORY_ALLOWED_DOMAINS="$(bashio::config 'allowed_domains')"
fi

# Optional dedicated key for signing artifact URLs (defaults to the bearer token if unset).
if bashio::config.has_value 'artifact_signing_key'; then
  export MEMORY_ARTIFACT_SIGNING_KEY="$(bashio::config 'artifact_signing_key')"
fi

# Public origin (e.g. https://memory.kazmin.tech) so artifacts_url returns absolute, shareable links.
if bashio::config.has_value 'public_base_url'; then
  export MEMORY_PUBLIC_BASE_URL="$(bashio::config 'public_base_url')"
fi

# Opt-in real-time MQTT publishing (note-change events) + Home Assistant stats sensors (MEMP-156/MEMP-056).
# Disabled by default: when mqtt_enabled is false nothing connects to a broker.
export MEMORY_MQTT_ENABLED="$(bashio::config 'mqtt_enabled')"
if bashio::config.true 'mqtt_enabled'; then
  export MEMORY_MQTT_HOST="$(bashio::config 'mqtt_host')"
  export MEMORY_MQTT_PORT="$(bashio::config 'mqtt_port')"
  export MEMORY_MQTT_TOPIC_PREFIX="$(bashio::config 'mqtt_topic_prefix')"
  if bashio::config.has_value 'mqtt_username'; then
    export MEMORY_MQTT_USERNAME="$(bashio::config 'mqtt_username')"
  fi
  if bashio::config.has_value 'mqtt_password'; then
    export MEMORY_MQTT_PASSWORD="$(bashio::config 'mqtt_password')"
  fi
  bashio::log.info "MQTT publishing enabled (host=${MEMORY_MQTT_HOST}:${MEMORY_MQTT_PORT}, prefix=${MEMORY_MQTT_TOPIC_PREFIX})"
else
  # Stated explicitly: with the switch off nothing downstream is even constructed, so silence here would be
  # indistinguishable from a broker that is merely unreachable.
  bashio::log.info "MQTT publishing disabled (mqtt_enabled = false); no Home Assistant sensors will be published."
fi

# Opt-in HTTP webhook for note-change events (MEMP-184). Disabled by default: with no URL nothing is posted.
if bashio::config.has_value 'webhook_url'; then
  export MEMORY_WEBHOOK_URL="$(bashio::config 'webhook_url')"
  if bashio::config.has_value 'webhook_secret'; then
    export MEMORY_WEBHOOK_SECRET="$(bashio::config 'webhook_secret')"
  fi
  bashio::log.info "Webhook publishing enabled (url=${MEMORY_WEBHOOK_URL})"
fi

# Advisory write-time adoption hints: recall-before-write nudge + post-write related-notes hint (MEMP-204/205). On by default.
export MEMORY_ADOPTION_HINTS="$(bashio::config 'adoption_hints')"

# Opt-in semantic recall (MEMP-196). Off by default: with it off no model is loaded and search stays lexical.
# The model is NOT in the image (it dwarfs the runtime and most users never switch this on), so the server
# downloads it in the background on first start and builds the index itself. Turning the option on is the whole
# procedure — search answers lexically in the meantime and turns semantic once the model lands.
export MEMORY_EMBEDDINGS="$(bashio::config 'embeddings_enabled')"
if bashio::config.true 'embeddings_enabled'; then
  export MEMORY_EMBEDDING_MODEL_DIR="$(bashio::config 'embedding_model_dir')"
  export MEMORY_EMBEDDING_WEIGHT="$(bashio::config 'embedding_weight')"
  if [ ! -f "${MEMORY_EMBEDDING_MODEL_DIR}/sentencepiece.bpe.model" ]; then
    bashio::log.info "Semantic recall is on; the model will be downloaded into ${MEMORY_EMBEDDING_MODEL_DIR} in the background. Search stays lexical until it is ready."
  else
    bashio::log.info "Semantic recall enabled (model=${MEMORY_EMBEDDING_MODEL_DIR}, weight=${MEMORY_EMBEDDING_WEIGHT})"
  fi
fi

bashio::log.info "Starting Memory MCP (HTTP on :8099, db=${MEMORY_DB_PATH})"
exec /opt/memory-mcp/MemoryMcp.Server
