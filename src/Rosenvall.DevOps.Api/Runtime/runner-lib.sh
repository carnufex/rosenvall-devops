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

rdo_remove_codex_reusable_auth_files() {
  codex_home="${1:-${CODEX_HOME:-}}"
  if [ -z "$codex_home" ]; then
    return 0
  fi

  rm -f \
    "$codex_home/auth.json" \
    "$codex_home/installation_id" \
    "$codex_home/config.toml" \
    "$codex_home/models_cache.json"
}

rdo_prepare_codex_runtime_home() {
  workspace="$1"
  launcher_home="${ROSENVALL_CODEX_LAUNCHER_HOME:-${CODEX_HOME:-}}"
  runtime_home="$workspace/codex-runtime-home"
  rm -rf "$runtime_home"
  mkdir -p "$runtime_home/tmp"
  if [ -n "$launcher_home" ]; then
    for file in auth.json config.toml installation_id models_cache.json; do
      if [ -f "$launcher_home/$file" ]; then
        cp -a "$launcher_home/$file" "$runtime_home/$file"
      fi
    done
    rdo_remove_codex_reusable_auth_files "$launcher_home"
  fi
  chmod 700 "$runtime_home/tmp"
  if [ -f "$runtime_home/auth.json" ]; then chmod 600 "$runtime_home/auth.json"; fi
  if [ -f "$runtime_home/config.toml" ]; then chmod 600 "$runtime_home/config.toml"; fi
  CODEX_HOME="$runtime_home"
  export CODEX_HOME
}

rdo_run_codex_without_repository_credentials() {
  workspace="$1"
  codex_command_file="$2"
  codex_log="$workspace/codex-output.log"
  unset ROSENVALL_GIT_TOKEN GITHUB_TOKEN
  rdo_prepare_codex_runtime_home "$workspace"
  set +e
  (
    env -i \
      PATH="${PATH:-/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin}" \
      HOME="${HOME:-/home/ubuntu}" \
      USER="${USER:-ubuntu}" \
      LOGNAME="${LOGNAME:-${USER:-ubuntu}}" \
      SHELL="${SHELL:-/bin/sh}" \
      LANG="${LANG:-C.UTF-8}" \
      TMPDIR="$CODEX_HOME/tmp" \
      CODEX_HOME="$CODEX_HOME" \
      CODEX_MODEL="${CODEX_MODEL:-}" \
      CODEX_REASONING_EFFORT="${CODEX_REASONING_EFFORT:-}" \
      ROSENVALL_CODEX_SESSION_ID="${ROSENVALL_CODEX_SESSION_ID:-}" \
      workspace="$workspace" \
      sh -c '. "$1"' sh "$codex_command_file"
  ) > "$codex_log" 2>&1 &
  codex_pid=$!
  sleep "${ROSENVALL_CODEX_AUTH_CLEANUP_DELAY_SECONDS:-2}"
  rdo_remove_codex_reusable_auth_files "$CODEX_HOME"
  wait "$codex_pid"
  codex_status=$?
  set -e
  rdo_remove_codex_reusable_auth_files "$CODEX_HOME"
  rdo_remove_codex_reusable_auth_files "${ROSENVALL_CODEX_LAUNCHER_HOME:-}"
  cat "$codex_log"
  if grep -Eiq 'bwrap|bubblewrap|No permissions to create a new namespace|unprivileged user namespaces' "$codex_log"; then
    echo "RDO_FAILURE=Codex runner sandbox is unavailable in this Kubernetes runner"
    return 26
  fi
  if [ "$codex_status" -ne 0 ]; then
    echo "RDO_FAILURE=Codex CLI failed"
    return 27
  fi
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
