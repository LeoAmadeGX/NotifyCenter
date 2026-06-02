#!/bin/sh

set -u

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR" || exit 1

ERRORS=0
WARNINGS=0

ok() {
  printf 'OK    %s\n' "$1"
}

warn() {
  WARNINGS=$((WARNINGS + 1))
  printf 'WARN  %s\n' "$1"
}

fail() {
  ERRORS=$((ERRORS + 1))
  printf 'ERROR %s\n' "$1"
}

check_file() {
  if [ -f "$1" ]; then
    ok "Found file: $1"
  else
    fail "Missing file: $1"
  fi
}

check_dir() {
  if [ -d "$1" ]; then
    ok "Found directory: $1"
  else
    fail "Missing directory: $1"
  fi
}

check_marker() {
  file_path=$1
  pattern=$2
  description=$3

  if [ ! -f "$file_path" ]; then
    fail "Cannot verify marker because file is missing: $file_path"
    return
  fi

  if grep -Fq "$pattern" "$file_path"; then
    ok "$description"
  else
    fail "$description is missing in $file_path"
  fi
}

read_env_value() {
  if [ ! -f .env ]; then
    printf ''
    return
  fi

  value=$(sed -n "s/^$1=//p" .env | tail -n 1 | tr -d '\r')
  value=$(printf '%s' "$value" | sed 's/^"//; s/"$//; s/^'\''//; s/'\''$//')
  printf '%s' "$value"
}

check_env_required() {
  key=$1
  guidance=$2
  value=$(read_env_value "$key")

  if [ -n "$value" ]; then
    ok ".env contains $key"
  else
    fail ".env is missing $key. $guidance"
  fi
}

check_env_not_placeholder() {
  key=$1
  placeholder=$2
  severity=$3
  guidance=$4
  value=$(read_env_value "$key")

  if [ -z "$value" ]; then
    if [ "$severity" = "warn" ]; then
      warn ".env does not define $key. $guidance"
    else
      fail ".env does not define $key. $guidance"
    fi
    return
  fi

  if [ "$value" = "$placeholder" ]; then
    if [ "$severity" = "warn" ]; then
      warn ".env still uses the example value for $key. $guidance"
    else
      fail ".env still uses the example value for $key. $guidance"
    fi
    return
  fi

  ok ".env has a non-placeholder value for $key"
}

check_jwt_key_length() {
  value=$(read_env_value "JWT_SIGNING_KEY")

  if [ -z "$value" ]; then
    fail ".env is missing JWT_SIGNING_KEY. Set a secret with at least 32 characters."
    return
  fi

  length=$(printf '%s' "$value" | wc -c | tr -d ' ')
  if [ "$length" -ge 32 ]; then
    ok "JWT_SIGNING_KEY length is $length"
  else
    fail "JWT_SIGNING_KEY is only $length characters long. Use at least 32 characters."
  fi
}

check_docker_compose_config() {
  if ! command -v docker >/dev/null 2>&1; then
    warn "docker command not found. Skipping Docker checks."
    return
  fi

  if ! docker compose version >/dev/null 2>&1; then
    fail "docker compose is not available."
    return
  fi

  if [ ! -f .env ]; then
    warn "Skipping docker compose config validation because .env is missing."
    return
  fi

  if docker compose config >/dev/null 2>&1; then
    ok "docker compose config renders successfully"
  else
    fail "docker compose config failed. Check docker-compose.yml and .env values."
  fi
}

check_app_shared_network() {
  if ! command -v docker >/dev/null 2>&1; then
    return
  fi

  if ! docker compose version >/dev/null 2>&1; then
    return
  fi

  if docker network inspect app-shared >/dev/null 2>&1; then
    ok "Docker network app-shared exists"
  else
    fail "Docker network app-shared does not exist. Create it with: docker network create app-shared"
  fi
}

check_build_artifact_noise() {
  if [ -d src/NotifyCenter.Api/bin ] || [ -d src/NotifyCenter.Api/obj ] || [ -d src/NotifyCenter.Web/node_modules ]; then
    warn "Build artifacts or node_modules are present in the bundle. They are not fatal, but a clean publish copy is recommended."
  else
    ok "Publish bundle is free of local build artifacts"
  fi
}

printf 'NotifyCenter publish preflight check\n'
printf 'Working directory: %s\n\n' "$SCRIPT_DIR"

check_file "docker-compose.yml"
check_file "api.Dockerfile"
check_file "web.Dockerfile"
check_file "nginx.conf"
check_file ".dockerignore"
check_file ".env.example"
check_file "README.md"

check_dir "src/NotifyCenter.Api"
check_dir "src/NotifyCenter.Web"

check_file "src/NotifyCenter.Api/NotifyCenter.Api.csproj"
check_file "src/NotifyCenter.Api/Program.cs"
check_file "src/NotifyCenter.Api/Auth/AdminSessionService.cs"
check_file "src/NotifyCenter.Api/Auth/JwtTokenService.cs"
check_file "src/NotifyCenter.Api/Auth/PasswordHasher.cs"
check_file "src/NotifyCenter.Api/Configuration/AppOptions.cs"
check_file "src/NotifyCenter.Api/Data/AdminUserRepository.cs"
check_file "src/NotifyCenter.Api/Data/NotificationDatabase.cs"
check_file "src/NotifyCenter.Api/Data/NotificationRepository.cs"
check_file "src/NotifyCenter.Api/Data/NotificationDeliveryRepository.cs"
check_file "src/NotifyCenter.Api/Data/RoutingTargetRepository.cs"
check_file "src/NotifyCenter.Api/Models/AdminContracts.cs"
check_file "src/NotifyCenter.Api/Models/NotificationContracts.cs"
check_file "src/NotifyCenter.Api/Models/NotificationQueryContracts.cs"
check_file "src/NotifyCenter.Api/Services/NotificationDispatcher.cs"
check_file "src/NotifyCenter.Api/Services/NotificationSenderRegistry.cs"
check_file "src/NotifyCenter.Api/Services/TelegramSender.cs"
check_file "src/NotifyCenter.Api/Services/AdminDashboardEventBroadcaster.cs"

check_file "src/NotifyCenter.Web/package.json"
check_file "src/NotifyCenter.Web/package-lock.json"
check_file "src/NotifyCenter.Web/index.html"
check_file "src/NotifyCenter.Web/vite.config.ts"
check_file "src/NotifyCenter.Web/src/App.vue"
check_file "src/NotifyCenter.Web/src/api.ts"
check_file "src/NotifyCenter.Web/src/types.ts"

check_marker "src/NotifyCenter.Api/Program.cs" 'app.MapGet("/api/admin/events"' "SSE admin events endpoint is present"
check_marker "src/NotifyCenter.Api/Program.cs" 'app.MapGet("/api/routing-targets"' "Routing target endpoints are present"
check_marker "src/NotifyCenter.Api/Program.cs" 'NotificationDeliveryRepository' "Delivery repository is wired into Program.cs"
check_marker "src/NotifyCenter.Api/Data/NotificationDeliveryRepository.cs" 'pending_no_target' "Delivery repository contains no-target delivery handling"
check_marker "src/NotifyCenter.Web/src/App.vue" 'new EventSource("/api/admin/events")' "Dashboard passive refresh is present"
check_marker "src/NotifyCenter.Web/src/App.vue" 'messageQuery' "Advanced message content filter is present"
check_marker "src/NotifyCenter.Web/src/api.ts" '/api/routing-targets' "Frontend routing target API calls are present"

if [ -f .env ]; then
  ok "Found file: .env"
  check_env_required "POSTGRES_PASSWORD" "Set your PostgreSQL password before deployment."
  check_env_required "JWT_SIGNING_KEY" "Set a strong JWT signing key before deployment."
  check_env_not_placeholder "POSTGRES_PASSWORD" "change-this-postgres-password" "error" "Replace it with your real PostgreSQL password."
  check_env_not_placeholder "JWT_SIGNING_KEY" "replace-with-a-32-character-secret" "error" "Replace it with a real secret."
  check_env_not_placeholder "TELEGRAM_BOT_TOKEN" "123456789:replace-with-your-bot-token" "warn" "If you plan to send Telegram notifications, replace it with a real bot token."
  check_env_not_placeholder "TELEGRAM_DEFAULT_CHAT_ID" "-1001234567890" "warn" "This field is now optional and mostly for compatibility; routing targets are preferred."
  check_jwt_key_length
else
  fail "Missing file: .env"
  warn "Copy .env.example to .env before deployment."
fi

check_build_artifact_noise
check_docker_compose_config
check_app_shared_network

printf '\nSummary: %s error(s), %s warning(s)\n' "$ERRORS" "$WARNINGS"

if [ "$ERRORS" -gt 0 ]; then
  printf 'Preflight failed. Fix the errors above before running docker compose up -d --build.\n'
  exit 1
fi

printf 'Preflight passed. You can continue with docker compose up -d --build.\n'
exit 0
