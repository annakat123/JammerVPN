import json,os,socket,struct,sys,time,statistics
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey,X25519PublicKey
from cryptography.hazmat.primitives.serialization import Encoding,PublicFormat
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

host=sys.argv[1]
def exact(c,n):
    out=b''
    while len(out)<n:
        part=c.recv(n-len(out))
        if not part:raise RuntimeError('Unexpected EOF')
        out+=part
    return out
def connect(small=False):
    c=socket.socket();c.settimeout(5);c.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
    if small:c.setsockopt(socket.SOL_SOCKET,socket.SO_RCVBUF,4096)
    c.connect((host,7777))
    key=X25519PrivateKey.generate();c.sendall(key.public_key().public_bytes(Encoding.Raw,PublicFormat.Raw))
    shared=key.exchange(X25519PublicKey.from_public_bytes(exact(c,32)))
    aes=AESGCM(HKDF(algorithm=hashes.SHA256(),length=32,salt=b'',info=b'jammer-session-key').derive(shared))
    encrypted=exact(c,struct.unpack('!I',exact(c,4))[0]);identifier=struct.unpack('<I',aes.decrypt(encrypted[:12],encrypted[12:],None))[0]
    assert 2<=identifier<=254
    return c,aes,identifier
def checksum(data):
    if len(data)%2:data+=b'\0'
    value=sum(struct.unpack('!%dH'%(len(data)//2),data))
    while value>>16:value=(value&65535)+(value>>16)
    return (~value)&65535
def packet(identifier,seq,size=64):
    data=bytes((i%251 for i in range(size-28)))
    icmp=struct.pack('!BBHHH',8,0,0,0x4a4d,seq)+data
    icmp=icmp[:2]+struct.pack('!H',checksum(icmp))+icmp[4:]
    header=struct.pack('!BBHHHBBH4s4s',0x45,0,size,seq,0,64,1,0,socket.inet_aton('172.16.0.%d'%identifier),socket.inet_aton('172.16.0.1'))
    header=header[:10]+struct.pack('!H',checksum(header))+header[12:]
    return header+icmp
def send(c,aes,data):
    nonce=os.urandom(12);encrypted=nonce+aes.encrypt(nonce,data,None)
    c.sendall(struct.pack('!I',len(encrypted))+encrypted)
def receive(c,aes):
    length=struct.unpack('!I',exact(c,4))[0]
    if not 0<length<=1528:raise RuntimeError('Invalid frame length')
    encrypted=exact(c,length)
    data=aes.decrypt(encrypted[:12],encrypted[12:],None)
    assert data[9]==1 and data[20]==0,'Expected echo reply'
    return struct.unpack('!H',data[26:28])[0]

mode=sys.argv[2]
if mode=='latency':
    rows=[]
    with connect()[0] as ignored: pass
    c,aes,identifier=connect()
    with c:
        time.sleep(.2)
        for i in range(8):
            c.setsockopt(socket.IPPROTO_TCP,socket.TCP_QUICKACK,0)
            start=time.perf_counter();send(c,aes,packet(identifier,i*2));time.sleep(.005);send(c,aes,packet(identifier,i*2+1))
            assert receive(c,aes)==i*2
            first=time.perf_counter();assert receive(c,aes)==i*2+1;end=time.perf_counter()
            rows.append(dict(first_ms=(first-start)*1000,second_ms=(end-start)*1000))
            time.sleep(.2)
    print(json.dumps(dict(mode=mode,samples=rows,first_median_ms=statistics.median(x['first_ms'] for x in rows),second_median_ms=statistics.median(x['second_ms'] for x in rows))))
elif mode=='backpressure':
    c,aes,identifier=connect(small=True)
    with c:
        count=10000
        for i in range(count):send(c,aes,packet(identifier,i,1400))
        time.sleep(.4)
        c.settimeout(2);received=set()
        try:
            while len(received)<count:received.add(receive(c,aes))
        except socket.timeout:pass
        print(json.dumps(dict(mode=mode,sent=count,received=len(received),missing=count-len(received))))
elif mode=='disconnect':
    for i in range(150):
        with socket.create_connection((host,7777),timeout=3) as c:c.setsockopt(socket.SOL_SOCKET,socket.SO_LINGER,struct.pack('ii',1,0))
    c,aes,identifier=connect()
    with c:
        send(c,aes,packet(identifier,1));assert receive(c,aes)==1
    print(json.dumps(dict(mode=mode,early_disconnects=150,final_echo='ok')))
