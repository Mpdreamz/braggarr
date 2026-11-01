#!/bin/sh
# Installs the latest braggarr into ~/.local/bin (or $BRAGGARR_INSTALL_DIR).
#   curl -fsSL https://raw.githubusercontent.com/Mpdreamz/braggarr/main/install.sh | sh
set -eu

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  *) echo "braggarr: no build for $(uname -s); on Windows, download braggarr-win-x64.zip from the releases page." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  arm64 | aarch64) arch=arm64 ;;
  *) echo "braggarr: no build for $(uname -m)." >&2; exit 1 ;;
esac
if [ "$os" = osx ] && [ "$arch" = x64 ]; then
  echo "braggarr: there is no Intel Mac build; use an Apple silicon Mac, or the container image." >&2
  exit 1
fi

dir="${BRAGGARR_INSTALL_DIR:-$HOME/.local/bin}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl -fsSL "https://github.com/Mpdreamz/braggarr/releases/latest/download/braggarr-$os-$arch.zip" -o "$tmp/braggarr.zip"
unzip -q "$tmp/braggarr.zip" -d "$tmp"
mkdir -p "$dir"
mv "$tmp/braggarr" "$dir/braggarr"
chmod +x "$dir/braggarr"
echo "braggarr is in $dir/braggarr"
case ":$PATH:" in
  *":$dir:"*) ;;
  *) echo "Add $dir to your PATH to run it as braggarr." ;;
esac
