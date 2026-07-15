#!/bin/sh
# Install apt packages, tolerating Ubuntu's flaky arm64 mirror.
#
# The arm64 image fetches from ports.ubuntu.com (Ubuntu's mirror for the non-x86 arches), whose three
# hosts (91.189.91.x) intermittently refuse connections - on GitHub's arm64 CI runner and from plenty of
# other networks. When that happens `apt-get update` only warns ("Some index files failed to download")
# and still exits 0, so the build dies a step later on `apt-get install` with "Unable to locate package".
# The amd64 image pulls from archive.ubuntu.com and is fine, so it's left untouched.
#
# Fix: list a couple of reliable, globally-reachable ports mirrors alongside ports.ubuntu.com in the
# sources. apt treats the space-separated URIs as a mirror set and gets each index from whichever answers,
# so a dead ports.ubuntu.com is transparently covered by the others and `install` finds its packages (the
# original break was that ports.ubuntu.com was the *only* source, so when it went down apt fetched nothing
# and install had no index to search). ports.ubuntu.com stays in the list as the canonical fallback for
# when the mirrors are the ones lagging. (azure.ports.ubuntu.com is deliberately not used: it's a bare
# alias for the same flaky 91.189.91.x IPs - no Azure mirror behind it - so it helps neither CI nor local
# builds.) A short outer loop then retries the whole thing so a momentary blip where every mirror is
# unreachable at once doesn't sink the build.
#
# Note: no APT::Update::Error-Mode=any here. It counts each (mirror, suite) index that failed to fetch as
# a hard error, which would fail `update` the moment the ports.ubuntu.com fallback is unreachable - the
# very case we're tolerating - even though every package is available from the other mirrors.
#
# UBUNTU_MIRROR (build arg, optional) is a space-separated list of extra mirror base URLs put first, for
# a build that wants to force its own mirror. Empty by default.
#
# Usage: apt-install <package>...
set -eu

src=/etc/apt/sources.list.d/ubuntu.sources

# Only ports.ubuntu.com (arm64) is unreliable; leave the amd64 archive/security sources alone.
if grep -q 'ports\.ubuntu\.com/ubuntu-ports' "$src"; then
    mirrors="${UBUNTU_MIRROR:-} http://mirror.us.leaseweb.net/ubuntu-ports/ https://mirrors.ocf.berkeley.edu/ubuntu-ports/"
    sed -i "s#^URIs: http://ports.ubuntu.com/ubuntu-ports/#URIs: $mirrors http://ports.ubuntu.com/ubuntu-ports/#" "$src"
fi

# Fail fast on a hung mirror so apt fails over to the next URI instead of blocking the build.
opts="-o Acquire::Retries=2 -o Acquire::http::Timeout=15 -o Acquire::https::Timeout=15"

for round in 1 2 3 4 5; do
    if apt-get $opts update \
        && apt-get $opts install -y --no-install-recommends "$@"; then
        rm -rf /var/lib/apt/lists/*
        exit 0
    fi
    echo "apt-install round $round failed on every mirror; waiting 15s before retry" >&2
    sleep 15
done

echo "apt-install: giving up after 5 rounds" >&2
exit 1
