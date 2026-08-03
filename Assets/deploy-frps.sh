#!/usr/bin/env bash
set -Eeuo pipefail

bind_port="${1:-}"
token="${2:-}"
version="${3:-0.65.0}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
archive="$script_dir/frps.tar.gz"

if [[ ! "$bind_port" =~ ^[0-9]+$ ]] || (( bind_port < 1 || bind_port > 65535 )); then
  echo '[ERROR] Invalid FRPS bind port.' >&2
  exit 2
fi
if [[ ! "$token" =~ ^[A-Za-z0-9._-]{16,128}$ ]]; then
  echo '[ERROR] Invalid token.' >&2
  exit 2
fi
if [[ ! -f "$archive" ]]; then
  echo '[ERROR] Embedded FRPS archive was not uploaded.' >&2
  exit 3
fi

case "$(uname -m)" in
  x86_64|amd64) arch='amd64' ;;
  aarch64|arm64) arch='arm64' ;;
  *) echo "[ERROR] Unsupported architecture: $(uname -m)" >&2; exit 4 ;;
esac

release_dir="frp_${version}_linux_${arch}"
install_dir="/opt/fridge/frp/${version}"
config_path="$install_dir/frps.yaml"
staged_config="$script_dir/frps.yaml"
log_path="/var/log/fridge-frps.log"

echo '[STEP] Extracting embedded FRPS package'
tar -xzf "$archive" -C "$script_dir"
test -x "$script_dir/$release_dir/frps"

cat > "$staged_config" <<EOF
bindPort: $bind_port

auth:
  method: "token"
  token: "$token"
EOF
chmod 0600 "$staged_config"

echo '[STEP] Verifying new FRPS configuration'
"$script_dir/$release_dir/frps" verify -c "$staged_config"

install -d -m 0755 "$install_dir"
if [[ -f "$config_path" ]]; then
  cp -a "$config_path" "$config_path.backup.$(date +%Y%m%d-%H%M%S)"
fi
install -m 0755 "$script_dir/$release_dir/frps" "$install_dir/frps"
install -m 0600 "$staged_config" "$config_path"
touch "$log_path"
chmod 0640 "$log_path"

cat > /etc/systemd/system/fridge-frps.service <<EOF
[Unit]
Description=Fridge FRP Server $version
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=$install_dir
ExecStart=$install_dir/frps -c $config_path
Restart=always
RestartSec=5s
LimitNOFILE=1048576
StandardOutput=append:$log_path
StandardError=append:$log_path

[Install]
WantedBy=multi-user.target
EOF

echo '[STEP] Starting fridge-frps.service'
systemctl daemon-reload
systemctl enable fridge-frps.service >/dev/null
systemctl restart fridge-frps.service
systemctl is-active --quiet fridge-frps.service

echo "[OK] FRPS $version is active on TCP port $bind_port"
echo "[INFO] Architecture: $arch"
echo "[INFO] Configuration: $config_path"
