#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DATA_DIR="${YELLOWFOX_DATA_DIR:-}"
if [ -z "${DATA_DIR}" ]; then
  if [ "$(uname -s)" = "Darwin" ]; then
    DATA_DIR="${HOME}/Library/Application Support/YellowFox"
  elif [ -n "${XDG_DATA_HOME:-}" ]; then
    DATA_DIR="${XDG_DATA_HOME}/YellowFox"
  else
    DATA_DIR="${HOME}/.local/share/YellowFox"
  fi
fi

export YELLOWFOX_DATA_DIR="${DATA_DIR}"

PYTHON_BIN="${DATA_DIR}/venv/bin/python"
if [ ! -x "${PYTHON_BIN}" ]; then
  PYTHON_BIN="${SCRIPT_DIR}/venv/bin/python"
fi
if [ ! -x "${PYTHON_BIN}" ]; then
  PYTHON_BIN="$(command -v python3 || command -v python || true)"
fi

if [ -z "${PYTHON_BIN}" ]; then
  echo "Python 3 was not found. Run setup-python.sh first." >&2
  exit 1
fi

exec "${PYTHON_BIN}" "${SCRIPT_DIR}/camoufox-server.py" "$@"
