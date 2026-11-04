import json,os,pathlib,re,subprocess,sys,time,uuid
root=pathlib.Path(sys.argv[1]).resolve()
if root.parent!=pathlib.Path('/var/tmp') or not root.name.startswith('jammer-isolated-'):raise RuntimeError('Unexpected test directory')
suffix=uuid.uuid4().hex[:8];a,b='jsc-'+suffix,'jss-'+suffix;va,vb='jca'+suffix,'jcb'+suffix
process=None
created=[]
def run(*args):subprocess.run(args,check=True,stdout=subprocess.DEVNULL)
def probe(mode):
    result=subprocess.run(['ip','netns','exec',a,'python3',str(root/'probe-isolated-jammer.py'),'198.18.0.2',mode],text=True,capture_output=True,timeout=60)
    if result.returncode:raise RuntimeError(result.stdout+'\n'+result.stderr)
    return json.loads(result.stdout)
def link_stats():
    return json.loads(subprocess.check_output(['ip','-n',b,'-j','-s','link','show','dev','JammerTun'],text=True))[0]['stats64']
def start(binary,label):
    global process
    log=open(root/(label+'.log'),'w')
    env=dict(os.environ,DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(root/'extract'))
    process=subprocess.Popen(['ip','netns','exec',b,str(root/binary)],cwd=root,env=env,stdin=subprocess.DEVNULL,stdout=log,stderr=subprocess.STDOUT)
    for _ in range(100):
        if process.poll() is not None:raise RuntimeError((root/(label+'.log')).read_text()[-3000:])
        ready=subprocess.check_output(['ip','netns','exec',b,'ss','-ltnH','sport = :7777'],text=True)
        if ready.strip():return
        time.sleep(.1)
    raise RuntimeError('Isolated server startup timed out')
def stop():
    global process
    if process is not None:
        process.terminate()
        try:process.wait(timeout=5)
        except subprocess.TimeoutExpired:process.kill();process.wait()
        process=None
try:
    run('ip','netns','add',a);created.append(a)
    run('ip','netns','add',b);created.append(b)
    run('ip','link','add',va,'type','veth','peer','name',vb)
    run('ip','link','set',va,'netns',a);run('ip','link','set',vb,'netns',b)
    for ns,dev,ip in [(a,va,'198.18.0.1/24'),(b,vb,'198.18.0.2/24')]:
        run('ip','-n',ns,'link','set','lo','up');run('ip','-n',ns,'addr','add',ip,'dev',dev);run('ip','-n',ns,'link','set',dev,'up')
        run('ip','netns','exec',ns,'tc','qdisc','add','dev',dev,'root','netem','delay','20ms')
    start('baseline','baseline')
    print(json.dumps(dict(case='running-release-copy',test=probe('latency'))),flush=True)
    stop()
    start('candidate','candidate')
    print(json.dumps(dict(case='candidate-nodelay',test=probe('latency'))),flush=True)
    for ns,dev in [(a,va),(b,vb)]:run('ip','netns','exec',ns,'tc','qdisc','del','dev',dev,'root')
    before=link_stats()
    result=probe('backpressure')
    after=link_stats()
    print(json.dumps(dict(case='candidate-backpressure',test=result,tun_rx_packets=after['rx']['packets']-before['rx']['packets'],tun_tx_packets=after['tx']['packets']-before['tx']['packets'],tun_rx_dropped=after['rx']['dropped']-before['rx']['dropped'],tun_tx_dropped=after['tx']['dropped']-before['tx']['dropped'])),flush=True)
    print(json.dumps(dict(case='candidate-early-disconnect',test=probe('disconnect'),server_alive=process.poll() is None)),flush=True)
    time.sleep(.1)
    counts=[int(value) for value in re.findall(r'queueDrops=(\d+)',(root/'candidate.log').read_text())]
    if max(counts,default=0)!=result['missing']:raise RuntimeError('Queue counter does not explain the missing replies')
    print(json.dumps(dict(case='candidate-queue-counter',maximum_dropped=max(counts,default=0))),flush=True)
finally:
    stop()
    for ns in created:subprocess.run(['ip','netns','del',ns],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    subprocess.run(['ip','link','del',va],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    print('Temporary namespaces removed; production service and interfaces were not changed.',flush=True)
