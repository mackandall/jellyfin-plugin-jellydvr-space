#!/usr/bin/env bash
# Patches jellyfin-web's index.html to load JellyDVR Space's "Move to archive…" menu entry (archive.js, served by
# the plugin at /JellyDvrSpace/web/archive.js). index.html is root-owned, hence a script run with sudo.
# Safe to re-run; re-run after every jellyfin-web package upgrade (it overwrites index.html).
#
# Usage: sudo ./install-web.sh [path-to-index.html]   (default /usr/share/jellyfin/web/index.html)
set -euo pipefail

WEB_INDEX="${1:-/usr/share/jellyfin/web/index.html}"
MARKER="<!-- JellyDvrSpace:archive:begin -->"
SNIPPET="$MARKER
<script defer src=\"/JellyDvrSpace/web/archive.js\"></script>
<!-- JellyDvrSpace:archive:end -->
"

[[ -f "$WEB_INDEX" ]] || { echo "error: $WEB_INDEX not found" >&2; exit 1; }
if grep -qF "$MARKER" "$WEB_INDEX"; then
    echo "Already patched: $WEB_INDEX"
    exit 0
fi

cp "$WEB_INDEX" "$WEB_INDEX.jellydvrspace-backup"
WEB_INDEX="$WEB_INDEX" SNIPPET="$SNIPPET" python3 - <<'PYEOF'
import os
path, snippet = os.environ["WEB_INDEX"], os.environ["SNIPPET"]
html = open(path, encoding="utf-8").read()
if "</body>" not in html:
    raise SystemExit("error: no </body> in " + path)
open(path, "w", encoding="utf-8").write(html.replace("</body>", snippet + "</body>", 1))
PYEOF
echo "Patched: $WEB_INDEX (reload the browser tab)"
