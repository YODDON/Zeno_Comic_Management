#!/bin/sh
set -eu

base_url="${BASE_URL:-http://localhost}"
curl --fail --silent --show-error "$base_url/health/live" >/dev/null
curl --fail --silent --show-error "$base_url/health/ready" >/dev/null
curl --fail --silent --show-error "$base_url/" >/dev/null
curl --fail --silent --show-error "$base_url/openapi/v1.json" >/dev/null
echo "Deployment smoke checks passed for $base_url."
