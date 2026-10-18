#!/bin/sh
set -eu
cd "$(dirname "$0")"
cmake -S . -B ../../work/native-macos -DCMAKE_BUILD_TYPE=Release '-DCMAKE_OSX_ARCHITECTURES=x86_64;arm64' -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0
cmake --build ../../work/native-macos --parallel 2
cmake --install ../../work/native-macos --prefix "$(cd ../../FamiStudio && pwd)"
cp ../../FamiStudio/NesSndEmu.dylib ../../Setup/FamiStudio.app/Contents/MacOS/
