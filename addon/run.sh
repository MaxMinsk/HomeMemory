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
fi

# Opt-in HTTP webhook for note-change events (MEMP-184). Disabled by default: with no URL nothing is posted.
if bashio::config.has_value 'webhook_url'; then
  export MEMORY_WEBHOOK_URL="$(bashio::config 'webhook_url')"
  if bashio::config.has_value 'webhook_secret'; then
    export MEMORY_WEBHOOK_SECRET="$(bashio::config 'webhook_secret')"
  fi
  bashio::log.info "Webhook publishing enabled (url=${MEMORY_WEBHOOK_URL})"
fi

bashio::log.info "Starting Memory MCP (HTTP on :8099, db=${MEMORY_DB_PATH})"
exec /opt/memory-mcp/MemoryMcp.Server
