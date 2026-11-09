#!/bin/bash
# usage: run.sh outdir [names...]   (builds then renders)
export PATH="$HOME/.dotnet:$PATH"
cd "$(dirname "$0")" && dotnet build -c Release 2>&1 | grep -E " error |rror\(s\)" | sort -u | head
dotnet bin/Release/net8.0/DialogGallery.dll "$@"
