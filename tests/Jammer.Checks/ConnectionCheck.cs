using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Jammer.Core;
using Jammer_desktop;

static class ConnectionCheck
{
    public static async Task Run()
    {
        var listener = new TcpListener(IPAddress.Loopback,7777);
        listener.Start();
        try
        {
            foreach (var scenario in new[]{"echo","closed","invalid-id","tun-error","route-error"})
            {
                var tun = new TestTun { ThrowOnRead = scenario == "tun-error" };
                var route = new TestRoute { ThrowOnRoute = scenario == "route-error" };
                var connection = new VpnConnection(()=>tun,()=>route);
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.Changed += ()=> { if(connection.IsConnected) connected.TrySetResult(); };
                var server = Task.Run(async ()=>
                {
                    using var socket = await listener.AcceptSocketAsync();
                    var key = await new Crypto.ECDH().CreateAesKey(socket);
                    await Frame.WriteFrameAsync(socket,Crypto.AES.Encrypt(BitConverter.GetBytes(scenario=="invalid-id"?999:42),key));
                    if (scenario is "tun-error" or "route-error")
                    {
                        try { await Frame.ReadFrameAsync(socket); } catch (Exception ex) when (ex is IOException or SocketException) { }
                        return;
                    }
                    if(scenario != "echo") return;
                    var data = Crypto.AES.Decrypt(await Frame.ReadFrameAsync(socket),key);
                    Check(data.SequenceEqual(tun.Packet),"original encrypted frame format reaches server");
                    await Frame.WriteFrameAsync(socket,Crypto.AES.Encrypt(data,key));
                    try { await Frame.ReadFrameAsync(socket); } catch(Exception ex) when(ex is IOException or SocketException) { }
                });
                var run = connection.ConnectAsync("127.0.0.1");
                if(scenario=="echo")
                {
                    await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var configuredSocket=(Socket)typeof(VpnConnection).GetField("_socket",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(connection)!;
                    Check(configuredSocket.NoDelay && configuredSocket.SendBufferSize>=4*1024*1024,"VPN socket keeps NoDelay and the configured send buffer");
                    await tun.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(route.Address=="172.16.0.42" && route.Dns,"assigned tunnel IP and DNS configured");
                    connection.Disconnect();
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(route.Cleaned && !connection.IsConnected && !connection.IsConnecting,"disconnect stops both loops and cleans routes");
                }
                else
                {
                    Exception? failure=null;
                    try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch(Exception ex){failure=ex;}
                    Check(failure is not null && failure is not TimeoutException,"unexpected closure or invalid ID is reported");
                    if (scenario == "tun-error") Check(failure is System.ComponentModel.Win32Exception { NativeErrorCode: 13 }, "TUN failure preserves native error code");
                    if (scenario == "route-error") Check(failure is InvalidOperationException, "route setup failure reaches caller");
                    Check(!connection.IsConnected && !connection.IsConnecting,"failed connection restores disconnected state");
                    if(scenario=="invalid-id") Check(route.Address is null,"invalid ID does not change OS routes");
                    else Check(route.Cleaned,"unexpected socket closure cleans routes");
                }
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { listener.Stop(); }
    }
    static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS: "+name);}
    sealed class TestTun : ITun
    {
        public byte[] Packet = new byte[]{0x45,1,2,3,4,5};
        public bool EchoExpected=true;
        public bool ThrowOnRead;
        public TaskCompletionSource Received=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool sent;
        public void InitializeTunnel() { }
        public void StartSession() { }
        public void ConfigureIpAddress(string ip,int mask) { }
        public byte[] ReceivePacket(){if(ThrowOnRead)throw new System.ComponentModel.Win32Exception(13);if(sent)return null!;sent=true;return Packet;}
        public void SendPacket(byte[] packet)
        {
            if(EchoExpected) Check(packet.SequenceEqual(Packet),"server response reaches TUN");
            else
            {
                int offset=(packet[0]&15)*4+8;
                Check(packet[9]==17 && packet.Length>offset+12 && packet[offset]==0x4a && packet[offset+1]==0x4d && (packet[offset+2]&0x80)!=0 && (packet[offset+3]&15)==0,"valid DNS reply from real server");
            }
            Received.TrySetResult();
        }
    }
    sealed class TestRoute : IRoute
    {
        public string? Address;
        public bool Dns,Cleaned;
        public bool ThrowOnRoute;
        public Action<string>? OnAddress;
        public void Route(string server,string client,string? iface,string? gateway){if(ThrowOnRoute)throw new InvalidOperationException("route setup failed");Address=client;OnAddress?.Invoke(client);}
        public void DNS(){Dns=true;}
        public void Clean(string server,string iface){Cleaned=true;}
    }
}
