#!/usr/bin/with-contenv bashio
set -euo pipefail

export MEMORY_TRANSPORT="http"
export MEMORY_DB_PATH="$(bashio::config 'db_path')"
export ASPNETCORE_URLS="http://0.0.0.0:8099"
export Logging__LogLevel__Default="$(bashio::config 'log_level')"

if bashio::config.has_value 'bearer_token'; then
  export MEMORY_BEARER_TOKEN="$(bashio::config 'bearer_token')"
else
  bashio::log.warning "No bearer_token set — the HTTP endpoint is unauthenticated. Set one for remote access."
fi

if bashio::config.has_value 'allowed_domains'; then
  export MEMORY_ALLOWED_DOMAINS="$(bashio::config 'allowed_domains')"
fi

# Public origin (e.g. https://memory.kazmin.tech) so artifacts_url returns absolute, shareable links.
if bashio::config.has_value 'public_base_url'; then
  export MEMORY_PUBLIC_BASE_URL="$(bashio::config 'public_base_url')"
fi

bashio::log.info "Starting Memory MCP (HTTP on :8099, db=${MEMORY_DB_PATH})"
exec /opt/memory-mcp/MemoryMcp.Server
