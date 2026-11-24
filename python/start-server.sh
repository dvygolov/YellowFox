#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PYTHON_BIN="${SCRIPT_DIR}/venv/bin/python"
if [ ! -x "${PYTHON_BIN}" ]; then
  PYTHON_BIN="$(command -v python3 || command -v python || true)"
fi

if [ -z "${PYTHON_BIN}" ]; then
  echo "Python 3 was not found. Run setup-python.sh first." >&2
  exit 1
fi

exec "${PYTHON_BIN}" "${SCRIPT_DIR}/camoufox-server.py" "$@"
