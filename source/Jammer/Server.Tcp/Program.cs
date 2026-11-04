using System.Net;
using System.Net.Sockets;
using System.Text;
using Jammer.Core;
using Serilog;
using Serilog.Core;

class Program
{
    public static int localPort { get; private set; }

    private static Socket tcpSocket;
    
    private static ITun _tun;

    private static ILogger _serverTcpLogger = Log.ForContext("SourceContext", "Server-Tcp");

    private static HashSet<int> clientIds = new HashSet<int>();

    private static TunDispatcher _tunDispatcher;
    async static Task Main(string[] args)
    {
        Logging.ConfigureLogger();
        
        _serverTcpLogger.Information("Введите порт для TCP соединения:");
        localPort = Convert.ToInt32(Console.ReadLine() switch{"" or null => "7777", string s => s}) ;

        _tun = ITun.CreateTun();
        
        _tun.InitializeTunnel();
        
        _tun.StartSession();
        
        _tun.ConfigureIpAddress("172.16.0.1", 24);
        
        _tunDispatcher = new TunDispatcher(_tun);
        
        _tunDispatcher.Start();
        
        await CreateTcpConnection();

        while (true)
        {
            Socket client = await tcpSocket.AcceptAsync();
            client.NoDelay = true;
            _serverTcpLogger.Information("клиент принят: {Client}", client.RemoteEndPoint);
            _ = HandleClientAsync(client);
            
        }
        
    }
    
    async static Task HandleClientAsync(Socket client)
    {
        int clientId = 0;
        ClientSession clientSession = null;
        try
        {
            Crypto.ECDH ecdh = new Crypto.ECDH();
            byte[] key = new byte[32];
            key= await ecdh.CreateAesKey(client);
                
            clientId = GenerateUniqueClientsIdentifier();
            byte[] clientIdBytes = BitConverter.GetBytes(clientId);
            await SendClientIdAsync(client, clientIdBytes, key);

            clientSession = new ClientSession {Key = key, Socket = client};
            _tunDispatcher.Add(clientId, clientSession);
            
            await Task.WhenAny(ReceiveMessageAsync(client, key), SendMessageAsync(clientSession));
            
        }
        catch (Exception ex)
        {
            _serverTcpLogger.Information("клиент отключился: {Ex}",ex.Message);
        }
        finally
        {
            _tunDispatcher.Remove(clientId);
            clientIds.Remove(clientId);
            client.Close();
            clientSession?.Queue.Writer.Complete();
            _serverTcpLogger.Information("{id} удален; queueDrops={QueueDrops}", clientId,
                clientSession is null ? 0 : Interlocked.Read(ref clientSession.QueueDrops));
        }
    }
    
    static int GenerateUniqueClientsIdentifier()
    {
        
        Random random = new Random();
        
        var clientId = random.Next(2, 255);
        if (clientIds.Contains(clientId))
        {
            clientId = GenerateUniqueClientsIdentifier();
        }
        else
        {
            clientIds.Add(clientId);
        }
        
        return clientId;
    }

    async static Task CreateTcpConnection()
    {
        tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("JAMMER_TCP_CONGESTION") == "bbr")
        {
            try
            {
                tcpSocket.SetRawSocketOption(6, 13, "bbr"u8); // IPPROTO_TCP / TCP_CONGESTION; inherited by accepted sockets.
                _serverTcpLogger.Information("TCP congestion control: bbr");
            }
            catch (SocketException ex)
            {
                _serverTcpLogger.Warning("BBR unavailable; using system TCP congestion control: {Error}", ex.SocketErrorCode);
            }
        }
        var serverEndPoint = new IPEndPoint(IPAddress.Any, localPort);
        
        try
        {
            tcpSocket.Bind(serverEndPoint);
            tcpSocket.Listen();
            _serverTcpLogger.Information("==========TCP соедение установлено=======");
        }
        catch (Exception ex)
        {
            _serverTcpLogger.Error("ошибка: {Ex}", ex.Message);
            Environment.Exit(1);
        }
    }
    async static Task ReceiveMessageAsync(Socket client, byte[] key)
    {
        while (client.Connected)
        {
            byte[] buffer = await Frame.ReadFrameAsync(client);

            var data = Crypto.AES.Decrypt(buffer, key);
            _tun.SendPacket(data);
            
            _serverTcpLogger.Debug("получено {Bytes} байт", data.Length);
        }
    }
    

    static Task SendMessageAsync(ClientSession clientSession) =>
        PacketSender.SendAsync(clientSession.Socket, clientSession.Key, clientSession.Queue.Reader);
    
    async static Task SendClientIdAsync(Socket client, byte[] input, byte[] key)
    {
        if (client.Connected)
        {
            var data = Crypto.AES.Encrypt(input, key);
            await Frame.WriteFrameAsync(client, data);
        }
    }
    
}

