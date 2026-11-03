#!/bin/bash
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin

# The downloaded binary is checked before this script runs.
if ! command -v nft >/dev/null || ! command -v python3 >/dev/null; then
    command -v apt-get >/dev/null || { echo 'Install nftables and python3 first' >&2; exit 1; }
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq nftables python3
fi
command -v ip >/dev/null
command -v ss >/dev/null
if systemctl is-active --quiet firewalld; then
    echo 'Active firewalld is not supported by this nftables installer' >&2; exit 1
fi
if command -v iptables >/dev/null && iptables --version | grep -q legacy; then
    echo 'Legacy iptables is not supported by this nftables installer' >&2; exit 1
fi
python3 /opt/jammer/setup-firewall.py.new --check

backup=/opt/jammer/backups/$(date +%Y%m%d-%H%M%S-%N)
mkdir -p "$backup"
for file in /opt/jammer/jammer /opt/jammer/setup-firewall.py /etc/systemd/system/jammer.service /etc/sysctl.d/99-jammer.conf; do
    if [ -f "$file" ]; then cp -p "$file" "$backup/$(basename "$file")"; fi
done
nft list ruleset > "$backup/firewall.nft"
echo "Backup: $backup"
was_active=0
if systemctl is-active --quiet jammer; then was_active=1; fi
old_forward=$(sysctl -n net.ipv4.ip_forward)
rollback() {
    status=$?
    trap - ERR
    set +e
    systemctl stop jammer
    for file in /opt/jammer/jammer /opt/jammer/setup-firewall.py /etc/systemd/system/jammer.service /etc/sysctl.d/99-jammer.conf; do
        if [ -f "$backup/$(basename "$file")" ]; then cp -p "$backup/$(basename "$file")" "$file"; fi
    done
    sysctl -qw "net.ipv4.ip_forward=$old_forward"
    systemctl daemon-reload
    if [ "$was_active" = 1 ]; then systemctl start jammer; else systemctl disable jammer; fi
    echo "Installation failed; previous files restored from $backup" >&2
    exit "$status"
}
trap rollback ERR
install -m 644 /opt/jammer/setup-firewall.py.new /opt/jammer/setup-firewall.py
python3 /opt/jammer/setup-firewall.py
printf 'net.ipv4.ip_forward=1\n' > /etc/sysctl.d/99-jammer.conf
sysctl -w net.ipv4.ip_forward=1
cat > /etc/systemd/system/jammer.service <<'EOF'
[Unit]
Description=Jammer VPN server
After=network-online.target nftables.service ufw.service
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/jammer
ExecStartPre=/usr/bin/python3 /opt/jammer/setup-firewall.py
ExecStart=/opt/jammer/jammer
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/opt/jammer/.net
Restart=on-failure
RestartSec=3
StandardInput=null
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
systemctl stop jammer || true
chmod 755 /opt/jammer/jammer.new
mv /opt/jammer/jammer.new /opt/jammer/jammer
systemctl daemon-reload
systemctl enable --now jammer
ready=0
for attempt in {1..20}; do
    pid=$(systemctl show jammer -p MainPID --value)
    if systemctl is-active --quiet jammer && [ "$pid" -gt 0 ] &&
        ss -H -ltnp 'sport = :7777' | grep -q "pid=$pid," &&
        ip -4 -o addr show dev JammerTun | grep -q '172.16.0.1/24'; then ready=1; break; fi
    sleep 1
done
if [ "$ready" != 1 ]; then
    journalctl -u jammer -n 15 --no-pager >&2
    echo 'Jammer failed readiness check (process/TCP7777/TUN)' >&2
    exit 1
fi
test "$(sysctl -n net.ipv4.ip_forward)" = 1
systemctl is-enabled --quiet jammer
echo 'Jammer ready: service, TCP/7777, TUN, forwarding and persistent firewall setup'
trap - ERR
