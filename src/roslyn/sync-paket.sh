#! /usr/bin/env bash

set -e

SCRIPT_DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )

(
  cd "$SCRIPT_DIR"
  bazel run @rules_dotnet//tools/paket2bazel -- \
    --dependencies-file $(pwd)/paket.dependencies \
    --output-folder $(pwd)/paket
)
