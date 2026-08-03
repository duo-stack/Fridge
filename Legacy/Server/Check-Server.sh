#!/usr/bin/env bash
set -u

echo "========================================"
echo "  FRP RDP Server Check"
echo "========================================"
echo

frps_path="$(readlink -f /usr/local/bin/frps 2>/dev/null || true)"
if [[ -z "$frps_path" || ! -x "$frps_path" ]]; then
  echo "[ERROR] /usr/local/bin/frps was not found."
  exit 1
fi

install_directory="$(dirname "$frps_path")"
config_path="$install_directory/frps.yaml"
log_path="$install_directory/frps.log"

echo "FRPS version:"
"$frps_path" --version

echo
if [[ -f "$config_path" ]]; then
  "$frps_path" verify -c "$config_path" || true
else
  echo "[ERROR] $config_path was not found."
fi

echo
echo "Service enabled: $(systemctl is-enabled frps.service 2>/dev/null || true)"
systemctl status frps.service --no-pager --lines=10 || true

echo
if command -v ss >/dev/null 2>&1; then
  echo "FRPS listeners (control and active proxy ports):"
  ss -lntup | grep -F 'frps' || echo "[WARN] No listener owned by frps was found. Run this check as root to see process ownership."
else
  echo "[WARN] ss was not found; listener check skipped."
fi

echo
echo "Recent log lines: $log_path"
if [[ -f "$log_path" ]]; then
  tail -n 30 "$log_path"
else
  echo "[WARN] Log file was not found."
fi
