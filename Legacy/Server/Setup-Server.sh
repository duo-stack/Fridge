#!/usr/bin/env bash
set -Eeuo pipefail

SERVER_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_SCRIPT="$SERVER_DIR/scripts/install-frps.sh"
FRP_VERSION='0.65.0'

if [[ $EUID -ne 0 ]]; then
  echo "[ERROR] Run this script as root: sudo ./Setup-Server.sh" >&2
  exit 1
fi

if [[ ! -f "$INSTALL_SCRIPT" ]]; then
  echo "[ERROR] Missing installer: $INSTALL_SCRIPT" >&2
  exit 1
fi

generate_token() {
  od -An -N24 -tx1 /dev/urandom | tr -d ' \n'
}

read_port() {
  local prompt="$1"
  local default_port="$2"
  local value

  while true; do
    read -r -p "$prompt [$default_port]: " value
    value="${value:-$default_port}"
    if [[ "$value" =~ ^[0-9]+$ ]] && (( value >= 1 && value <= 65535 )); then
      printf '%s\n' "$value"
      return 0
    fi
    echo "Enter a port from 1 to 65535." >&2
  done
}

echo "========================================"
echo "  FRP RDP Server Setup"
echo "========================================"
echo "FRP version is fixed at $FRP_VERSION."
echo

FRP_BIND_PORT="$(read_port 'FRPS control port' 7000)"
while true; do
  FRP_RDP_PORT="$(read_port 'Public RDP forwarding port (TCP/UDP)' 3389)"
  if [[ "$FRP_RDP_PORT" != "$FRP_BIND_PORT" ]]; then
    break
  fi
  echo "The control port and RDP forwarding port must be different."
done

default_token="$(generate_token)"
while true; do
  read -r -p "Authentication token (Enter to generate one): " FRP_TOKEN
  FRP_TOKEN="${FRP_TOKEN:-$default_token}"
  if [[ "$FRP_TOKEN" =~ ^[A-Za-z0-9._-]{16,128}$ ]]; then
    break
  fi
  echo "Use 16-128 characters: letters, numbers, dot, underscore, or hyphen."
done

export SERVER_DIR FRP_VERSION FRP_TOKEN FRP_BIND_PORT FRP_RDP_PORT

# Source the module so the token is not placed in a child process command line.
# shellcheck source=scripts/install-frps.sh
source "$INSTALL_SCRIPT"

echo
echo "========================================"
echo "  Server setup completed"
echo "========================================"
echo "FRP version:       $FRP_VERSION"
echo "FRPS control port: $FRP_BIND_PORT/TCP"
echo "RDP public port:   $FRP_RDP_PORT/TCP and UDP"
echo "Client token:      $FRP_TOKEN"
echo "Install directory: $FRP_INSTALL_DIRECTORY"
echo "Configuration:     $FRP_INSTALL_DIRECTORY/frps.yaml"
echo "Log file:          $FRP_INSTALL_DIRECTORY/frps.log"
echo
echo "Configure these ports in the cloud security group, then use the same ports and token on the Windows client."
