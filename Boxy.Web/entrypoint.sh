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

# GPU passthrough for hardware video encoding (VAAPI). When a DRI device is mapped in
# (compose: `devices: - /dev/dri:/dev/dri`), add appuser to the group that owns each node so ffmpeg can
# open the render node. The gid varies by host - Debian's render group is 104, other distros and NAS
# kernels differ - so it's read off the device rather than hardcoded, the way linuxserver.io images do.
# A no-op when no GPU is passed through, so it's safe to leave in unconditionally.
for dev in /dev/dri/renderD* /dev/dri/card*; do
    [ -e "$dev" ] || continue
    dev_gid=$(stat -c '%g' "$dev" 2>/dev/null) || continue
    [ "$dev_gid" = "0" ] && continue   # never widen appuser into the root group
    dev_grp=$(getent group "$dev_gid" | cut -d: -f1)
    if [ -z "$dev_grp" ]; then
        dev_grp="gpu$dev_gid"
        groupadd -o -g "$dev_gid" "$dev_grp" 2>/dev/null || true
    fi
    usermod -aG "$dev_grp" appuser 2>/dev/null || true
done

cd /app

exec gosu appuser "$@"
