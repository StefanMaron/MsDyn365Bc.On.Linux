#!/usr/bin/env bash
# Tests for scripts/install-al-compiler.sh with a fake nupkg, no network and no
# dotnet. Run: bash scripts/test-install-al-compiler.sh
set -u
here="$(cd "$(dirname "$0")" && pwd)"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fail=0

# A fake .Linux nupkg with an executable alc and a large file, so a concurrent
# extraction has time to read it half-written.
mkdir -p "$work/pkg/lib/net8.0"
printf '#!/bin/bash\necho fake-alc\n' > "$work/pkg/lib/net8.0/alc"
chmod +x "$work/pkg/lib/net8.0/alc"
head -c 50000000 /dev/urandom > "$work/pkg/lib/net8.0/big.dll"
(cd "$work/pkg" && zip -q -r "$work/fake.nupkg" .)
sha=$(sha256sum "$work/pkg/lib/net8.0/big.dll" | cut -d' ' -f1)

mkdir -p "$work/bin"
cat > "$work/bin/curl" <<EOF
#!/bin/bash
# like curl: truncate the target, then write it over a second; a second
# download while one runs is the race that corrupted the compiler
while [ \$# -gt 0 ]; do
  if [ "\$1" = -o ]; then
    mkdir "$work/downloading" 2>/dev/null || echo overlap >> "$work/overlaps"
    : > "\$2"; sleep 1; cp "$work/fake.nupkg" "\$2"
    rmdir "$work/downloading" 2>/dev/null
    exit 0
  fi
  shift
done
exit 1
EOF
# records the fds it inherits: a child that keeps the lock fd open would
# hold the lock after the install exits
cat > "$work/bin/dotnet" <<EOF
#!/bin/bash
ls /proc/\$\$/fd >> "$work/dotnet-fds"
exit 1
EOF
chmod +x "$work/bin/curl" "$work/bin/dotnet"

install() {
  mkdir -p "$work/job$1"
  HOME="$work/home" PATH="$work/bin:$PATH" RUNNER_TEMP="$work/job$1" \
    bash "$here/install-al-compiler.sh" 28.3 "${2:-17.0.34.45391}" > "$work/out.$1" 2> "$work/err.$1"
}

mkdir -p "$work/home"
for i in 1 2 3 4; do install "$i" & done
wait

cache="$work/home/.al-tool-cache/17.0.34.45391"
if [ ! -e "$work/overlaps" ] \
   && [ "$(sha256sum "$cache/lib/net8.0/big.dll" | cut -d' ' -f1)" = "$sha" ] \
   && ! grep -qiE "invalid compressed|bad zipfile" "$work"/err.*; then
  echo "ok   four concurrent installs leave one intact compiler"
else
  echo "FAIL concurrent installs downloaded or extracted over each other"
  fail=1
fi

for i in 1 2 3 4; do
  grep -q "^AL_TOOL_DIR=$cache/lib/net8.0$" "$work/out.$i" || { echo "FAIL install $i: no AL_TOOL_DIR"; fail=1; }
done
[ $fail = 0 ] && echo "ok   every install reports the compiler"

touch -d '2000-01-01' "$cache/lib/net8.0/big.dll"
install 5
if [ "$(stat -c %Y "$cache/lib/net8.0/big.dll")" = "$(date -d 2000-01-01 +%s)" ]; then
  echo "ok   a complete install is reused, not extracted again"
else
  echo "FAIL a complete install was extracted again"; fail=1
fi

rm -f "$cache/.complete"
echo garbage > "$cache/lib/net8.0/big.dll"
install 6
if [ "$(sha256sum "$cache/lib/net8.0/big.dll" | cut -d' ' -f1)" = "$sha" ]; then
  echo "ok   an install without its completion mark is replaced"
else
  echo "FAIL an incomplete install was kept"; fail=1
fi

if [ -s "$work/dotnet-fds" ] && ! grep -qx 9 "$work/dotnet-fds"; then
  echo "ok   child processes do not inherit the lock"
else
  echo "FAIL a child process inherited the lock fd"; fail=1
fi

install 7 17.0.34.45391 & install 8 16.0.1.1 & wait
w7=$(sed -n 's/^AL_BIN_DIR=//p' "$work/out.7")
w8=$(sed -n 's/^AL_BIN_DIR=//p' "$work/out.8")
if [ -n "$w7" ] && [ "$w7" != "$w8" ] \
   && grep -q 17.0.34.45391 "$w7/AL" && grep -q 16.0.1.1 "$w8/AL"; then
  echo "ok   each job gets its own AL wrapper"
else
  echo "FAIL two jobs share one AL wrapper ($w7, $w8)"; fail=1
fi

exit $fail
