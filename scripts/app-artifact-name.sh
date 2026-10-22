#!/usr/bin/env bash
# app-artifact-name.sh — file name stem for the .app compiled from an app dir.
#
# Designed to be SOURCED:
#
#   . "$REPO_DIR/scripts/app-artifact-name.sh"
#   app_artifact_name "apps/Sales/app"     # -> apps-Sales-app
#
# The compile, publish, artifact and JUnit steps all derive a file name from
# the app dir. The basename alone is not unique when several apps share a
# folder name, as in a monorepo laid out apps/<Name>/app + apps/<Name>/test:
# every app would be written to build/app.app and overwrite the previous one,
# and the next compile fails with AL1022 for the dependency it just lost.
#
# The dir is used as given, relative to the consumer's repo; a leading
# "project/" (the checkout dir in the GitHub workflow) and "./" are dropped so
# the name is the same in every step, and a trailing slash is ignored.

app_artifact_name() {
  local d="${1%/}"
  d="${d#project/}"
  d="${d#./}"
  printf '%s\n' "${d//\//-}"
}
