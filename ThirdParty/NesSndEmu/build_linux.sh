#!/bin/sh
set -eu
cd "$(dirname "$0")"
cmake -S . -B ../../work/native-linux -DCMAKE_BUILD_TYPE=Release
cmake --build ../../work/native-linux --parallel 2
cmake --install ../../work/native-linux --prefix "$(cd ../../FamiStudio && pwd)"
