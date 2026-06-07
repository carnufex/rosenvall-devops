#!/bin/sh
set -eu

rdo_json_escape() {
  printf '%s' "$1" | tr '\r\n' '  ' | sed 's/\\/\\\\/g; s/"/\\"/g'
}

rdo_prepare_git_askpass() {
  workspace="$1"
  mkdir -p "$workspace"
  cat > "$workspace/git-askpass.sh" <<'EOF'
#!/bin/sh
case "$1" in
  *Username*) printf '%s' "${GIT_USERNAME:-x-access-token}" ;;
  *Password*) printf '%s' "${GIT_PASSWORD:-}" ;;
  *) printf '\n' ;;
esac
EOF
  chmod 700 "$workspace/git-askpass.sh"
}

rdo_git_with_repository_credentials() {
  workspace="$1"
  provider="$2"
  token="$3"
  shift 3
  username="x-access-token"
  if [ "$provider" = "LocalGit" ]; then
    username="${ROSENVALL_LOCAL_GIT_USERNAME:-rdo}"
  fi
  rdo_prepare_git_askpass "$workspace"
  GIT_ASKPASS="$workspace/git-askpass.sh" GIT_TERMINAL_PROMPT=0 GIT_USERNAME="$username" GIT_PASSWORD="$token" "$@"
}

rdo_collect_changed_files() {
  workspace="$1"
  git status --porcelain | sed 's/^...//' | sed 's#.* -> ##' > "$workspace/uncommitted-files.txt"
  git diff --name-only "${ROSENVALL_DEFAULT_BRANCH:-HEAD}"...HEAD > "$workspace/committed-files.txt"
  cat "$workspace/uncommitted-files.txt" "$workspace/committed-files.txt" | sed '/^$/d' | sort -u > "$workspace/changed-files.txt"
}

rdo_collect_uncommitted_files() {
  workspace="$1"
  git status --porcelain | sed 's/^...//' | sed 's#.* -> ##' > "$workspace/changed-files.txt"
}
