#!/bin/sh
set -eu
mkdir -p "${DATA_DIR:-/data}"
python -m app.passwords
exec uvicorn app.main:create_app --factory --host 0.0.0.0 --port 9000
