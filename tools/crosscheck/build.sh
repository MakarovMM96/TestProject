#!/usr/bin/env bash
# Builds the cross-check against libsai and regenerates test fixtures.
# Usage: tools/crosscheck/build.sh [path-to-libsai-checkout]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
LIBSAI="${1:-$HERE/libsai}"
if [ ! -d "$LIBSAI" ]; then
  git clone --depth 1 https://github.com/Wunkolo/libsai "$LIBSAI"
fi
OUT="$HERE/../../tests/Sai2Recovery.Tests/Fixtures"
mkdir -p "$OUT"
# libsai has one sanity assert (remaining blob >= 3*channels*width bytes) that is
# not a format rule and fires on tiny or highly compressible tiles. Drop only it.
grep -v "assert(DpcmTableData.size_bytes() >= RowReadSize)" "$LIBSAI/source/sai2.cpp" > "$HERE/sai2_ref.cpp"
g++ -std=c++20 -O1 -g -UNDEBUG -I"$LIBSAI/include" \
  "$HERE/crosscheck.cpp" "$HERE/sai2_ref.cpp" -o "$HERE/crosscheck"
"$HERE/crosscheck" "$OUT"
# Keep only SHA-256 of libsai's decoded pixels in the repo
python3 - "$OUT" <<'PY'
import glob, hashlib, json, os, sys
out = sys.argv[1]
files = sorted(glob.glob(os.path.join(out, "*.bgra")))
d = {os.path.basename(f): hashlib.sha256(open(f, "rb").read()).hexdigest() for f in files}
json.dump(d, open(os.path.join(out, "expected-sha256.json"), "w"), indent=2)
for f in files: os.remove(f)
print(d)
PY
