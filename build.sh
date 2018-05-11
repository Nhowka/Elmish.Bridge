#!/usr/bin/env bash

set -eu

cd "$(dirname "$0")"

PAKET_BOOTSTRAPPER_EXE=.paket/paket.exe
PAKET_EXE=.paket/paket.exe
FAKE_EXE=packages/build/FAKE/tools/FAKE.exe

FSIARGS=""
FSIARGS2=""
OS=${OS:-"unknown"}

echo $OSTYPE
if [ "$OS" != "Windows_NT" ]
then
  # Can't use FSIARGS="--fsiargs -d:MONO" in zsh, so split it up
  # (Can't use arrays since dash can't handle them)
  FSIARGS="--fsiargs"
  FSIARGS2="-d:MONO"

  # Allows NETFramework like net45 to be built using dotnet core tooling with mono
  export FrameworkPathOverride=$(dirname $(which mono))/../lib/mono/4.5/

fi

run() {
  if [ "$OS" != "Windows_NT" ]
  then
    mono "$@"
  else
    "$@"
  fi
}



run $PAKET_EXE restore

[ ! -e build.fsx ] && run $PAKET_EXE update
[ ! -e build.fsx ] && run $FAKE_EXE init.fsx

# build template
# pushd Content/
# ./build.sh
# ./build.sh Clean
# popd

run $FAKE_EXE "$@" $FSIARGS $FSIARGS2 build.fsx
