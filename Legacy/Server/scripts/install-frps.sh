#!/usr/bin/env bash

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  echo "[ERROR] Run ../Setup-Server.sh instead of this module directly." >&2
  exit 1
fi

: "${SERVER_DIR:?}"
: "${FRP_VERSION:?}"
: "${FRP_TOKEN:?}"
: "${FRP_BIND_PORT:?}"

case "$(uname -m)" in
  x86_64|amd64) frp_arch='amd64' ;;
  aarch64|arm64) frp_arch='arm64' ;;
  *)
    echo "[ERROR] Unsupported CPU architecture: $(uname -m)" >&2
    return 1
    ;;
esac

archive_name="frp_${FRP_VERSION}_linux_${frp_arch}.tar.gz"
release_dir="frp_${FRP_VERSION}_linux_${frp_arch}"
local_archive="$SERVER_DIR/$archive_name"
download_url="https://github.com/fatedier/frp/releases/download/v${FRP_VERSION}/${archive_name}"
install_directory="/opt/frp/$release_dir"
config_path="$install_directory/frps.yaml"
log_path="$install_directory/frps.log"
temp_dir="$(mktemp -d)"

cleanup_frps_install() {
  rm -rf -- "$temp_dir"
}
trap cleanup_frps_install RETURN

if [[ -f "$local_archive" ]]; then
  echo "[INFO] Using local archive: $local_archive"
  cp -- "$local_archive" "$temp_dir/$archive_name"
else
  echo "[INFO] Downloading $download_url"
  if command -v curl >/dev/null 2>&1; then
    curl --fail --location --retry 3 --output "$temp_dir/$archive_name" "$download_url"
  elif command -v wget >/dev/null 2>&1; then
    wget --output-document="$temp_dir/$archive_name" "$download_url"
  else
    echo "[ERROR] Install curl or wget, or place $archive_name beside Setup-Server.sh." >&2
    return 1
  fi
fi

tar -xzf "$temp_dir/$archive_name" -C "$temp_dir"
install -d -m 0755 "$install_directory"

if [[ -f "$config_path" ]]; then
  cp -a "$config_path" "$config_path.backup.$(date +%Y%m%d-%H%M%S)"
fi

install -m 0755 "$temp_dir/$release_dir/frps" "$install_directory/frps"
if [[ -f "$temp_dir/$release_dir/LICENSE" ]]; then
  install -m 0644 "$temp_dir/$release_dir/LICENSE" "$install_directory/LICENSE"
fi

cat > "$config_path" <<EOF
bindPort: $FRP_BIND_PORT

auth:
  method: "token"
  token: "$FRP_TOKEN"
EOF
chmod 0600 "$config_path"
touch "$log_path"
chmod 0640 "$log_path"

"$install_directory/frps" verify -c "$config_path"
ln -sfn "$install_directory/frps" /usr/local/bin/frps

cat > /etc/systemd/system/frps.service <<EOF
[Unit]
Description=FRP Server 0.65.0
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=$install_directory
ExecStart=$install_directory/frps -c $config_path
Restart=always
RestartSec=5s
LimitNOFILE=1048576
StandardOutput=append:$log_path
StandardError=append:$log_path

[Install]
WantedBy=multi-user.target
EOF

cat > /etc/logrotate.d/frps <<EOF
$log_path {
    daily
    rotate 7
    compress
    missingok
    notifempty
    copytruncate
}
EOF

systemctl daemon-reload
systemctl enable frps.service
systemctl restart frps.service

if ! systemctl is-active --quiet frps.service; then
  systemctl status frps.service --no-pager || true
  tail -n 50 "$log_path" || true
  echo "[ERROR] frps.service is not running." >&2
  return 1
fi

FRP_INSTALL_DIRECTORY="$install_directory"
export FRP_INSTALL_DIRECTORY

echo "[OK] frps.service is enabled and running."
systemctl status frps.service --no-pager --lines=5
