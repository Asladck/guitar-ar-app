#!/bin/sh
# Checks the strike, gesture, rhythm and calibration logic without Unity: those files are plain
# C#. Needs Mono's compiler (apt-get install mono-mcs, or brew install mono).
set -e
export LANG=C.UTF-8 LC_ALL=C.UTF-8
cd "$(dirname "$0")"
out="${TMPDIR:-/tmp}/guitar-logic-tests.exe"
mcs -nologo -langversion:experimental -out:"$out" LogicTests.cs ../../Assets/Scripts/Logic/*.cs
mono "$out"
