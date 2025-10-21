#!/bin/bash
# deploy.sh — idempotent deployment of a Dockerized app to a remote server
set -e
set -o pipefail

# =========== CONFIGURATION ===========
GIT_REPO=""                        # e.g. github.com/your/repo.git
PERSONAL_ACCESS_TOKEN=""
BRANCH_NAME="main"

REMOTE_USER="user"
REMOTE_HOST="remote.server.com"
SSH_KEY="$HOME/.ssh/id_rsa"
APP_PORT=8080
APP_NAME="myapp"                   # container / image name
REMOTE_APP_DIR="~/app_repo"

LOG_FILE="deploy_$(date +%Y%m%d_%H%M%S).log"
CLEANUP_MODE=false
# =====================================

# Parse optional flag
if [[ "${1:-}" == "--cleanup" ]]; then
  CLEANUP_MODE=true
fi

# Simple logger (writes to console and log file)
log() {
  echo "$(date '+%Y-%m-%d %H:%M:%S') | $1" | tee -a "$LOG_FILE"
}

# Trap for unexpected errors
on_error() {
  local rc=$?
  log "❌ Unexpected error (exit code $rc). See $LOG_FILE for details."
  exit $rc
}
trap on_error ERR

log "🟢 Starting deployment script (idempotent). Log: $LOG_FILE"
log "Target host: $REMOTE_HOST, app name: $APP_NAME, port: $APP_PORT"

# =========== Local sanity checks ===========
if [ -z "$GIT_REPO" ]; then
  log "❌ GIT_REPO not set. Export or edit script."
  exit 1
fi

if [ ! -f "$SSH_KEY" ]; then
  log "❌ SSH key not found at $SSH_KEY"
  exit 1
fi

# Clone or update repo locally
if [ -d "app_repo" ]; then
  log "📦 Updating local repository..."
  cd app_repo
  git pull origin "$BRANCH_NAME"
else
  log "📥 Cloning repository..."
  git clone -b "$BRANCH_NAME" "https://$PERSONAL_ACCESS_TOKEN@${GIT_REPO}" app_repo
  cd app_repo
fi

# Ensure there is a Dockerfile or docker-compose.yml
if [ -f "Dockerfile" ]; then
  DEPLOY_MODE="dockerfile"
  log "✅ Dockerfile found (deploy_mode=$DEPLOY_MODE)."
elif [ -f "docker-compose.yml" ]; then
  DEPLOY_MODE="compose"
  log "✅ docker-compose.yml found (deploy_mode=$DEPLOY_MODE)."
else
  log "❌ Neither Dockerfile nor docker-compose.yml found."
  exit 1
fi

# Basic connectivity test
log "🔍 Checking network connectivity to $REMOTE_HOST..."
if ! ping -c 2 "$REMOTE_HOST" >/dev/null 2>&1; then
  log "❌ Cannot reach $REMOTE_HOST (ping failed)."
  exit 1
fi
log "✅ Host reachable."

# =========== Copy files to remote ===========
log "📤 Copying app files to remote host..."
# create remote app dir first (idempotent) then copy
ssh -i "$SSH_KEY" "$REMOTE_USER@$REMOTE_HOST" "mkdir -p $REMOTE_APP_DIR" >/dev/null
scp -i "$SSH_KEY" -r ./* "$REMOTE_USER@$REMOTE_HOST:$REMOTE_APP_DIR/" >>"$LOG_FILE" 2>&1
log "✅ Files copied."

# Create Nginx config locally (with escaped nginx variables) and transfer
NGINX_TMP="./nginx_config.tmp"
cat > "$NGINX_TMP" <<EOF
server {
    listen 80;
    server_name $REMOTE_HOST;
    location / {
        proxy_pass http://localhost:$APP_PORT;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
}
EOF
scp -i "$SSH_KEY" "$NGINX_TMP" "$REMOTE_USER@$REMOTE_HOST:~/nginx_config.tmp" >>"$LOG_FILE" 2>&1
rm -f "$NGINX_TMP"

# =========== Remote deployment block ===========
log "🔑 Running deployment commands on remote host..."
ssh -i "$SSH_KEY" "$REMOTE_USER@$REMOTE_HOST" bash -s -- "$APP_NAME" "$APP_PORT" "$DEPLOY_MODE" "$CLEANUP_MODE" "$REMOTE_APP_DIR" <<'REMOTE_EOF'
set -e
set -o pipefail

APP_NAME="$1"
APP_PORT="$2"
DEPLOY_MODE="$3"
CLEANUP_MODE="$4"
REMOTE_APP_DIR="$5"

timestamp() { date '+%Y-%m-%d %H:%M:%S'; }

echo "$(timestamp) | Remote: starting (host=$(hostname))"

# Install dependencies (idempotent installs)
sudo apt update -y
sudo apt install -y docker.io docker-compose nginx

sudo systemctl enable --now docker
sudo systemctl enable --now nginx

# If cleanup mode, remove deployed resources and exit
if [ "$CLEANUP_MODE" = "true" ]; then
  echo "$(timestamp) | Remote: Cleanup mode ON — removing docker containers, images, networks, app folder and nginx config."
  sudo docker ps -aq | xargs -r sudo docker stop || true
  sudo docker ps -aq | xargs -r sudo docker rm || true
  sudo docker images -q | xargs -r sudo docker rmi -f || true
  sudo docker network prune -f || true
  sudo rm -rf $REMOTE_APP_DIR || true
  # remove site config only if it matches our expected file (safe-guard)
  if [ -f /etc/nginx/sites-available/default ]; then
    # Overwrite with a minimal default that returns 404 instead of removing package-managed files
    sudo bash -c 'cat > /etc/nginx/sites-available/default <<NGCONF
server {
    listen 80;
    server_name _;
    return 404;
}
NGCONF'
    sudo systemctl restart nginx || true
  fi
  echo "$(timestamp) | Remote: Cleanup complete."
  exit 0
fi

# Idempotent pre-deploy cleanup of old app container & image
if sudo docker ps -a --format '{{.Names}}' | grep -q "^${APP_NAME}$"; then
  echo "$(timestamp) | Remote: Stopping and removing existing container ${APP_NAME}..."
  sudo docker stop "${APP_NAME}" || true
  sudo docker rm "${APP_NAME}" || true
fi

if sudo docker images -q "${APP_NAME}" >/dev/null 2>&1; then
  echo "$(timestamp) | Remote: Removing existing image ${APP_NAME}..."
  sudo docker rmi -f "${APP_NAME}" || true
fi

# Ensure docker network exists (idempotent)
if sudo docker network ls --format '{{.Name}}' | grep -q "^app_network$"; then
  echo "$(timestamp) | Remote: Docker network app_network exists — using it."
else
  echo "$(timestamp) | Remote: Creating docker network app_network..."
  sudo docker network create app_network
fi

# Move into app dir
cd "$REMOTE_APP_DIR" || (echo "$(timestamp) | Remote: ERROR: app dir not found"; exit 1)

# Build & run according to mode
if [ "$DEPLOY_MODE" = "dockerfile" ]; then
  echo "$(timestamp) | Remote: Building Docker image ${APP_NAME}..."
  sudo docker build -t "${APP_NAME}" .
  # remove any previous container with same name already handled above
  echo "$(timestamp) | Remote: Running container ${APP_NAME}..."
  sudo docker run -d --name "${APP_NAME}" --network app_network -p "${APP_PORT}:80" "${APP_NAME}" >/dev/null
elif [ "$DEPLOY_MODE" = "compose" ]; then
  echo "$(timestamp) | Remote: Deploying via docker-compose..."
  # ensure compose brings down previous run without error
  sudo docker-compose down || true
  sudo docker-compose up -d --build
else
  echo "$(timestamp) | Remote: Unknown deploy mode: $DEPLOY_MODE"
  exit 1
fi

# Install Nginx config idempotently (we uploaded a temp file earlier)
if [ -f ~/nginx_config.tmp ]; then
  sudo mv ~/nginx_config.tmp /etc/nginx/sites-available/default
  sudo nginx -t
  sudo systemctl restart nginx
  echo "$(timestamp) | Remote: Nginx configured & restarted."
else
  echo "$(timestamp) | Remote: No nginx_config.tmp uploaded — skipping Nginx config."
fi

# Validation checks: docker, container, nginx, local HTTP
sudo systemctl is-active --quiet docker && echo "$(timestamp) | Remote: Docker active" || (echo "$(timestamp) | Remote: Docker NOT active"; exit 1)

echo "$(timestamp) | Remote: Containers:"
sudo docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

if sudo docker ps --format '{{.Names}}' | grep -q "^${APP_NAME}$"; then
  echo "$(timestamp) | Remote: App container ${APP_NAME} running."
else
  echo "$(timestamp) | Remote: ERROR: App container ${APP_NAME} not found."
  exit 1
fi

sudo systemctl is-active --quiet nginx && echo "$(timestamp) | Remote: Nginx active" || (echo "$(timestamp) | Remote: Nginx NOT active"; exit 1)

# Test local endpoint via curl on remote
if curl -s -I "http://localhost:${APP_PORT}" | head -n1 | grep -q "200\|301\|302"; then
  echo "$(timestamp) | Remote: Local endpoint returned success."
else
  echo "$(timestamp) | Remote: Local endpoint failed!"
  exit 1
fi

echo "$(timestamp) | Remote: Deployment finished successfully."
REMOTE_EOF

# =========== Local validation ===========
log "🌐 Performing remote accessibility check from local machine..."
if curl -s -I "http://$REMOTE_HOST" | head -n1 | grep -q "200\|301\|302"; then
  log "✅ Remote endpoint responsive: http://$REMOTE_HOST"
else
  log "❌ Remote endpoint not responding (HTTP). Check $LOG_FILE for remote logs."
  exit 1
fi

log "🎉 Deployment completed successfully. Log saved: $LOG_FILE"
exit 0