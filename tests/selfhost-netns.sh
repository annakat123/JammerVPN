#!/bin/bash
set -euo pipefail
source_dir=$(cd "$(dirname "$0")/../source/Jammer/Jammer.Deploy" && pwd)
dir=$(mktemp -d /var/tmp/jammer-firewall-test.XXXXXX)
cp "$source_dir/setup-firewall.py" "$source_dir/install-server.sh" "$dir/"
client=jv-client-$$
server=jv-server-$$
external=jv-external-$$
listener=''
cleanup() {
    if [ -n "$listener" ]; then kill "$listener" 2>/dev/null || true; fi
    for ns in "$client" "$server" "$external"; do ip netns del "$ns" 2>/dev/null || true; done
}
trap cleanup EXIT
for ns in "$client" "$server" "$external"; do ip netns add "$ns"; ip -n "$ns" link set lo up; done
ip -n "$server" link add JammerTun type veth peer name cli
ip -n "$server" link set cli netns "$client"
ip -n "$server" link add wanTest type veth peer name ext
ip -n "$server" link set ext netns "$external"
ip -n "$server" addr add 172.16.0.1/24 dev JammerTun
ip -n "$server" addr add 192.0.2.1/30 dev wanTest
ip -n "$server" link set JammerTun up
ip -n "$server" link set wanTest up
ip -n "$server" route add default via 192.0.2.2
ip -n "$client" addr add 172.16.0.2/24 dev cli
ip -n "$client" link set cli up
ip -n "$client" route add default via 172.16.0.1
ip -n "$external" addr add 192.0.2.2/30 dev ext
ip -n "$external" addr add 1.1.1.1/32 dev lo
ip -n "$external" link set ext up
cat > "$dir/base.nft" <<'EOF'
table inet existing {
 chain input { type filter hook input priority 0; policy drop; iifname "lo" accept; ct state established,related accept; tcp dport 22 accept comment "existing-ssh"; }
 chain forward { type filter hook forward priority 0; policy drop; }
}
table ip another {
 chain input { type filter hook input priority 10; policy drop; }
 chain forward { type filter hook forward priority 10; policy drop; }
}
EOF
ip netns exec "$server" nft -f "$dir/base.nft"
if ip netns exec "$client" ping -c 1 -W 1 1.1.1.1 >/dev/null; then echo 'FAIL: initial firewall allowed traffic'; exit 1; fi
ip netns exec "$server" python3 "$dir/setup-firewall.py" --check
ip netns exec "$server" python3 "$dir/setup-firewall.py"
ip netns exec "$client" ping -c 2 -W 1 1.1.1.1 >/dev/null
echo 'PASS: forwarding and NAT through two DROP base chains'
ip netns exec "$server" python3 -c 'import socket; s=socket.socket(); s.bind(("0.0.0.0",7777)); s.listen(); c,_=s.accept(); c.sendall(b"ok"); c.close(); s.close()' &
listener=$!
sleep .3
ip netns exec "$client" python3 -c 'import socket; s=socket.create_connection(("172.16.0.1",7777),2); assert s.recv(2)==b"ok"; s.close()'
wait "$listener"
listener=''
echo 'PASS: TCP/7777 accessible through both input DROP chains'
ip netns exec "$server" python3 "$dir/setup-firewall.py"
count=$(ip netns exec "$server" nft list ruleset | grep -c 'comment "JammerVPN:')
test "$count" = 7
ip netns exec "$server" nft list ruleset | grep -q 'existing-ssh'
echo 'PASS: repeat installation does not duplicate rules; existing SSH rule preserved'
ip -n "$server" link set wanTest down
ip -n "$server" link set wanTest name wanRenamed
ip -n "$server" link set wanRenamed up
ip -n "$server" route replace default via 192.0.2.2 dev wanRenamed
ip netns exec "$server" python3 "$dir/setup-firewall.py"
ip netns exec "$client" ping -c 1 -W 1 1.1.1.1 >/dev/null
echo 'PASS: WAN interface is detected again after renaming'
# Only this disposable namespace is reset, never the host firewall.
ip netns exec "$server" nft flush ruleset
ip netns exec "$server" nft -f "$dir/base.nft"
ip netns exec "$server" sysctl -qw net.ipv4.ip_forward=0
ip netns exec "$server" python3 "$dir/setup-firewall.py"
ip netns exec "$client" ping -c 1 -W 1 1.1.1.1 >/dev/null
echo 'PASS: rules and forwarding recovered after simulated firewall/state reset'
bash -n "$dir/install-server.sh"
echo 'PASS: installer shell syntax'
