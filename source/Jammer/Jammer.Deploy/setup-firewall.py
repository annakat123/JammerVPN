#!/usr/bin/env python3
"""Apply only Jammer's IPv4 rules; preserve other firewall rules and policies."""
import json
import re
import subprocess
import sys


def run(*args):
    return subprocess.check_output(args, text=True)


def identifier(value):
    if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_.-]*', value):
        raise SystemExit('Unsupported nftables table/chain name: ' + value)
    return value


route = json.loads(run('ip', '-j', '-4', 'route', 'get', '1.1.1.1'))[0]
wan = route['dev']
if wan in ('lo', 'JammerTun'):
    raise SystemExit('No external IPv4 interface found')
ruleset = json.loads(run('nft', '-j', 'list', 'ruleset'))['nftables']
commands = []
for item in ruleset:
    rule = item.get('rule', {})
    if str(rule.get('comment', '')).startswith('JammerVPN:'):
        commands.append('delete rule {} {} {} handle {}'.format(
            rule['family'], identifier(rule['table']), identifier(rule['chain']), rule['handle']))

for item in ruleset:
    chain = item.get('chain', {})
    if chain.get('family') not in ('ip', 'inet') or chain.get('type') != 'filter':
        continue
    target = '{} {} {}'.format(chain['family'], identifier(chain['table']), identifier(chain['name']))
    if chain.get('hook') == 'input':
        ipv4 = 'meta nfproto ipv4 ' if chain['family'] == 'inet' else ''
        commands.append(f'insert rule {target} {ipv4}tcp dport 7777 counter accept comment "JammerVPN:input"')
    elif chain.get('hook') == 'forward':
        commands.extend([
            f'insert rule {target} iifname "JammerTun" oifname {json.dumps(wan)} ip saddr 172.16.0.0/24 counter accept comment "JammerVPN:out"',
            f'insert rule {target} iifname {json.dumps(wan)} oifname "JammerTun" ip daddr 172.16.0.0/24 ct state established,related counter accept comment "JammerVPN:in"',
        ])

if any(x.get('table', {}).get('family') == 'ip' and x.get('table', {}).get('name') == 'jammer_vpn' for x in ruleset):
    commands.append('delete table ip jammer_vpn')
commands.extend([
    'add table ip jammer_vpn',
    'add chain ip jammer_vpn postrouting { type nat hook postrouting priority srcnat; policy accept; }',
    f'add rule ip jammer_vpn postrouting ip saddr 172.16.0.0/24 oifname {json.dumps(wan)} counter masquerade comment "JammerVPN:nat"',
])
batch = '\n'.join(commands) + '\n'
subprocess.run(['nft', '-c', '-f', '-'], input=batch, text=True, check=True)
if '--check' not in sys.argv:
    subprocess.run(['nft', '-f', '-'], input=batch, text=True, check=True)
    run('sysctl', '-w', 'net.ipv4.ip_forward=1')
print(f'Jammer firewall/NAT: external interface {wan}')
