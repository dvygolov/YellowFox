#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ACTIVATE_SCRIPT="${SCRIPT_DIR}/venv/bin/activate"

if [ -f "${ACTIVATE_SCRIPT}" ]; then
  # shellcheck disable=SC1090
  source "${ACTIVATE_SCRIPT}"
fi

python "${SCRIPT_DIR}/camoufox-server.py" "$@"
