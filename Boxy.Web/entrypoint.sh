#!/bin/sh
set -e

PUID=${PUID:-888}
PGID=${PGID:-888}

# Temporarily set home to /root to avoid issues with /data not being mounted yet
usermod -d /root appuser

# Remap appuser/appgroup to requested PUID/PGID (allow non-unique)
groupmod -o -g "$PGID" appgroup
usermod -o -u "$PUID" appuser

# Restore home directory
usermod -d /data appuser

mkdir -p /data /data/storage
chown -R appuser:appgroup /app || echo "Warning: Could not set ownership on /app. Remote or read-only mount?"
chown -R appuser:appgroup /data || echo "Warning: Could not set ownership on /data. Remote or read-only mount?"
chmod 755 /data || true

cd /app

exec gosu appuser "$@"
