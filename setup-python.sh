#!/usr/bin/env bash
# Creates the per-user Python environment and installs the Camoufox runtime
# for macOS/Linux. Safe to run from inside a read-only .app bundle or package.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON_DIR="${SCRIPT_DIR}/python"
if [ ! -d "${PYTHON_DIR}" ]; then
  echo "Python scripts were not found at ${PYTHON_DIR}." >&2
  exit 1
fi

PYTHON_BIN="${PYTHON:-python3}"
if ! command -v "${PYTHON_BIN}" >/dev/null 2>&1; then
  echo "Python 3 was not found. Install python3 (for example: brew install python@3.12 or apt install python3 python3-venv)." >&2
  exit 1
fi

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

VENV_DIR="${DATA_DIR}/venv"
mkdir -p "${DATA_DIR}"

if [ ! -x "${VENV_DIR}/bin/python" ]; then
  echo "Creating virtual environment at ${VENV_DIR}..."
  "${PYTHON_BIN}" -m venv "${VENV_DIR}"
fi

VENV_PYTHON="${VENV_DIR}/bin/python"

echo "Installing dependencies..."
"${VENV_PYTHON}" -m pip install --upgrade pip
"${VENV_PYTHON}" -m pip uninstall -y camoufox || true
"${VENV_PYTHON}" -m pip install -r "${PYTHON_DIR}/requirements.txt"

echo "Installing the Camoufox browser..."
PYTHONPATH="${PYTHON_DIR}${PYTHONPATH:+:${PYTHONPATH}}" "${VENV_PYTHON}" "${PYTHON_DIR}/install-camoufox-browser.py"

echo
echo "Setup complete."
echo "Python environment: ${VENV_DIR}"
echo "Camoufox browsers:  ${DATA_DIR}/camoufox"
