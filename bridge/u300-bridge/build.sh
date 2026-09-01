#!/usr/bin/env bash
# Builds the U300 bridge into build/u300-bridge.jar
# Requires a JDK 11 or later on PATH. No build tool needed.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

rm -rf build/classes
mkdir -p build/classes

# Java's own classpath wildcard, not the shell's. Quoted so the shell leaves it
# alone, and relative so it works whether javac is a native Windows binary or a
# POSIX one -- a ';'-joined list of MSYS paths is understood by neither.
find src -name '*.java' > build/sources.txt

echo "Compiling $(wc -l < build/sources.txt) source file(s)..."
javac --release 11 -Xlint:-options -cp "libs/*" -d build/classes @build/sources.txt

# Class-Path entries resolve relative to the jar, not the working directory,
# so the dependencies are copied in beside it. build/ is then a self-contained
# deployable: copy the folder, run the jar, done.
mkdir -p build/libs build/native
cp libs/*.jar build/libs/
cp native/* build/native/ 2>/dev/null || true

cp_entries="$(cd build/libs && ls ./*.jar | sed 's|^\./|libs/|' | tr '\n' ' ')"

{
  echo "Manifest-Version: 1.0"
  echo "Main-Class: com.warehouse.u300bridge.Main"
  echo "Class-Path: $cp_entries"
  echo
} > build/MANIFEST.MF

jar --create --file build/u300-bridge.jar --manifest build/MANIFEST.MF -C build/classes .

rm -f build/sources.txt

echo
echo "Built: build/u300-bridge.jar"
echo "Run:   java -jar build/u300-bridge.jar bridge.properties"
