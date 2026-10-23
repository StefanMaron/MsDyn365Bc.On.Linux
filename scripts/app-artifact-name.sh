#!/usr/bin/env bash
# app-artifact-name.sh — file name stem for the .app compiled from an app dir.
#
# Designed to be SOURCED:
#
#   . "$REPO_DIR/scripts/app-artifact-name.sh"
#   app_artifact_name "src/MyApp" "src/MyApp" "src/MyTest"   # -> MyApp
#   app_artifact_name "apps/Sales/app" "apps/Sales/app" "apps/Shipping/app"
#                                                            # -> apps-Sales-app
#
# Arguments: the app dir, then every app and test app dir of the run, as the
# consumer listed them (relative to its repo, without the checkout prefix).
#
# The compile, publish, artifact and JUnit steps all derive a file name from
# the app dir. The name is the folder's basename, as it always was. Only when
# another dir of the same run has the same basename, as in a monorepo laid out
# apps/<Name>/app + apps/<Name>/test, is the whole path used instead, with "/"
# turned into "-". Otherwise every such app would be written to build/app.app,
# overwrite the previous one, and the next compile would fail with AL1022.
# A leading "./" and a trailing "/" are ignored.

_app_artifact_dir() {
  local d="${1%/}"
  printf '%s' "${d#./}"
}

app_artifact_name() {
  local d other
  d=$(_app_artifact_dir "$1")
  shift
  for other in "$@"; do
    [ -z "$other" ] && continue
    other=$(_app_artifact_dir "$other")
    if [ "$other" != "$d" ] && [ "${other##*/}" = "${d##*/}" ]; then
      printf '%s\n' "${d//\//-}"
      return
    fi
  done
  printf '%s\n' "${d##*/}"
}
