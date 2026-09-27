#!/usr/bin/env bash
# Builds self-contained Joe Pro bundles: the IDE (joepro-ide), the command line (joepro), the application runner
# (joepro-app) and the Data Server (joepro-server), for each runtime identifier given (default: this machine's).
#
#   tools/package.sh                         # this platform
#   tools/package.sh linux-x64 win-x64 osx-arm64
#
# Output: dist/joepro-<version>-<rid>.tar.gz (.zip for Windows): the four programs sharing one .NET runtime, and a README.
# No .NET install is needed to run them.
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
if [ $# -eq 0 ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) set -- linux-x64 ;; Linux-aarch64) set -- linux-arm64 ;;
    Darwin-arm64) set -- osx-arm64 ;; Darwin-x86_64) set -- osx-x64 ;;
    *) set -- win-x64 ;;
  esac
fi

mkdir -p dist
for rid in "$@"; do
  name="joepro-$version-$rid"
  out="dist/$name"
  rm -rf "$out"
  echo "== $name"
  for project in JoePro.Ide JoePro.Cli JoePro.AppHost JoePro.Server; do
    dotnet publish "src/$project" -c Release -r "$rid" --self-contained true \
      -p:DebugType=none \
      -o "$out" -v quiet -nologo
  done
  cat > "$out/README.txt" <<TXT
Joe Pro $version ($rid)

  joepro-ide      the IDE: Command Window, designers, debugger
  joepro          command line: REPL, run programs, import FoxPro applications, packages, sync
  joepro-app      runs a built application (.jpapp)
  joepro-server   the Data Server for multi-user databases

Nothing needs installing: put this folder anywhere and run the programs.
Documentation: https://github.com/abounaderjoe/foxpro-copycat/tree/main/docs
TXT
  (cd dist && if [[ "$rid" == win-* ]]; then rm -f "$name.zip"; zip -qr "$name.zip" "$name"; else tar -czf "$name.tar.gz" "$name"; fi)
  echo "   $(ls dist/"$name".* | grep -v '/$')"
done
