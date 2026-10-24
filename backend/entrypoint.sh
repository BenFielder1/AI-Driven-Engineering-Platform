#!/bin/sh
# AIDE API container entrypoint (FR-106, D-V3-10).
#
# Started as root (Railway, plain `docker run`): the /data volume may be a fresh, root-owned mount. Create the keys and
# workspaces folders, hand them to $APP_UID, then drop to $APP_UID with setpriv. The app never runs as root.
# Started non-root (Kubernetes: runAsNonRoot, runAsUser 1654, fsGroup owns the volume): run directly.
#
# With no arguments, or arguments that aren't a command (app switches like --urls), this starts the API. A command as
# the first argument (e.g. /app/efbundle --connection ...) runs that instead, as $APP_UID when started as root.
set -eu

APP_UID="${APP_UID:-1654}"
DATA_DIR="${AIDE_DATA_DIR:-/data}"

if [ "$#" -eq 0 ] || ! command -v "$1" >/dev/null 2>&1; then
    set -- dotnet /app/Aide.Api.dll "$@"
fi

if [ "$(id -u)" != "0" ]; then
    exec "$@"
fi

mkdir -p "$DATA_DIR/keys" "$DATA_DIR/workspaces"
# The mount point itself: never recursive (the volume may be remounted root-owned on every deploy).
chown "$APP_UID:$APP_UID" "$DATA_DIR"
for dir in "$DATA_DIR/keys" "$DATA_DIR/workspaces"; do
    # Walk a tree only while it isn't ours yet (fresh, or written as root before); afterwards this is one stat.
    if [ "$(stat -c %u "$dir")" != "$APP_UID" ]; then
        chown -R "$APP_UID:$APP_UID" "$dir"
    fi
done

exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups -- "$@"
