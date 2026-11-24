#!/usr/bin/env bash
# Creates the Python environment and installs the Camoufox runtime on macOS/Linux.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}/python"

PYTHON_BIN="${PYTHON:-python3}"
if ! command -v "${PYTHON_BIN}" >/dev/null 2>&1; then
  echo "Python 3 was not found. Install python3 (for example: brew install python@3.12 or apt install python3 python3-venv)." >&2
  exit 1
fi

if [ ! -d venv ]; then
  echo "Creating virtual environment..."
  "${PYTHON_BIN}" -m venv venv
fi

# shellcheck disable=SC1091
source venv/bin/activate

echo "Installing dependencies..."
python -m pip install --upgrade pip
python -m pip uninstall -y camoufox || true
python -m pip install -r requirements.txt

echo "Installing the Camoufox browser..."
python install-camoufox-browser.py

echo
echo "Setup complete. Virtual environment: ${SCRIPT_DIR}/python/venv"
