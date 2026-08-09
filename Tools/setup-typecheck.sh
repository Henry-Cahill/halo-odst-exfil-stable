#!/usr/bin/env bash
# One-time setup for Tools/typecheck.sh on a fresh Linux container.
# Installs a user-local .NET 8 SDK and unpacks Unity reference assemblies.
# No root required. Safe to re-run.

set -euo pipefail

TOOLS="${DROPSHOCK_TOOLS:-$HOME/tools}"
mkdir -p "$TOOLS"

if [ ! -d "$HOME/.dotnet/sdk" ]; then
  echo "==> Installing .NET 8 SDK to ~/.dotnet"
  curl -sSL -o "$TOOLS/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
  chmod +x "$TOOLS/dotnet-install.sh"
  "$TOOLS/dotnet-install.sh" --channel 8.0 --install-dir "$HOME/.dotnet" --no-path
else
  echo "==> .NET SDK already present, skipping"
fi

if [ ! -d "$TOOLS/refs" ] || [ -z "$(ls -A "$TOOLS/refs" 2>/dev/null)" ]; then
  echo "==> Fetching Unity reference assemblies"
  # NOTE: 2021.3.33 is the newest UnityEngine.Modules published to NuGet. The
  # project is on 6000.5.6f1. See the caveat block in Tools/typecheck.sh — this is
  # the single biggest limitation of checking this project outside the editor.
  curl -sSL -o "$TOOLS/ue.nupkg" \
    "https://api.nuget.org/v3-flatcontainer/unityengine.modules/2021.3.33/unityengine.modules.2021.3.33.nupkg"

  # A .nupkg is a zip. unzip is not installed and there is no root to install it;
  # node is present, so use it rather than adding a system dependency.
  node -e "
const fs=require('fs'),zlib=require('zlib');
const b=fs.readFileSync(process.argv[1]);
let eocd=-1; for(let i=b.length-22;i>=0;i--){if(b.readUInt32LE(i)===0x06054b50){eocd=i;break}}
const n=b.readUInt16LE(eocd+10); let off=b.readUInt32LE(eocd+16); let out=0;
const dir=process.argv[2];
fs.mkdirSync(dir,{recursive:true});
for(let k=0;k<n;k++){
  const nl=b.readUInt16LE(off+28), el=b.readUInt16LE(off+30), cl=b.readUInt16LE(off+32);
  const name=b.toString('utf8',off+46,off+46+nl);
  const lho=b.readUInt32LE(off+42), method=b.readUInt16LE(off+10), csize=b.readUInt32LE(off+20);
  if(name.endsWith('.dll')){
    const lnl=b.readUInt16LE(lho+26), lel=b.readUInt16LE(lho+28);
    const ds=lho+30+lnl+lel; const data=b.subarray(ds,ds+csize);
    fs.writeFileSync(dir+'/'+name.split('/').pop(), method===8?zlib.inflateRawSync(data):data);
    out++;
  }
  off+=46+nl+el+cl;
}
console.log('    extracted '+out+' reference assemblies');
" "$TOOLS/ue.nupkg" "$TOOLS/refs"
else
  echo "==> Reference assemblies already present, skipping"
fi

echo
echo "Done. Run:  ./Tools/typecheck.sh 9.0"
