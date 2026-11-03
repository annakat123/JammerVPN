using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Renci.SshNet;
using Serilog;

namespace Jammer.Deploy;

public class SshService
{
    private const string ReleaseUrl = "https://github.com/Vimer5410/Jammer/releases/download/v0.1.0/jammer-linux-server-x64";
    private const string ReleaseHash = "81c5c33dd8ac6a9962affdd7547016cc19af3bb33b37c1fc744ad2b3a6a17f68";
    private static readonly ILogger Logger = Log.ForContext("SourceContext", "SshService");

    public static async Task DeploySelfHosted(string ip, int port, string name, string password)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Укажите корректный IPv4-адрес сервера.");
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Проверьте SSH-порт, логин и пароль.");

        await Task.Run(async () =>
        {
            using var ssh = new SshClient(ip, port, name, password);
            ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
            ssh.Connect();
            Run(ssh, "test \"$(id -u)\" = 0 && test \"$(uname -m)\" = x86_64 && test -c /dev/net/tun && command -v systemctl >/dev/null",
                "Нужны root, Linux x86_64, systemd и /dev/net/tun");
            Run(ssh, "mkdir -p /opt/jammer", "Создание каталога");
            Logger.Information("Загрузка серверного релиза Jammer…");
            using var download = ssh.CreateCommand($"curl -fSL --connect-timeout 15 --max-time 120 -o /opt/jammer/jammer.new {ReleaseUrl}");
            download.CommandTimeout = TimeSpan.FromSeconds(140);
            download.Execute();
            using var sftp = new SftpClient(ip, port, name, password);
            sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
            sftp.Connect();
            if (download.ExitStatus != 0)
            {
                Logger.Warning("Сервер не скачал релиз; загружаем через SFTP");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                using var source = await http.GetStreamAsync(ReleaseUrl);
                await sftp.UploadFileAsync(source, "/opt/jammer/jammer.new");
            }
            Run(ssh, $"echo '{ReleaseHash}  /opt/jammer/jammer.new' | sha256sum -c -", "Проверка SHA-256 релиза");
            foreach (string file in new[] { "setup-firewall.py", "install-server.sh" })
            {
                using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Jammer.Deploy.{file}")
                    ?? throw new InvalidOperationException($"Нет ресурса {file}");
                using var reader = new StreamReader(source);
                using var normalized = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reader.ReadToEnd().ReplaceLineEndings("\n")));
                sftp.UploadFile(normalized, $"/opt/jammer/{file}.new");
            }
            Logger.Information("Настройка firewall, NAT и службы Jammer…");
            Run(ssh, "bash /opt/jammer/install-server.sh.new", "Установка сервера");
            Logger.Information("Проверка TCP/7777 и DNS-запроса через туннель…");
            await ServerProbe.CheckAsync(address);
            Logger.Information("Установка завершена: TCP/7777 доступен, запрос и ответ DNS прошли через VPN, forwarding и NAT.");
        });
    }

    private static void Run(SshClient ssh, string command, string stage)
    {
        using var cmd = ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(3);
        var output = cmd.Execute().Trim();
        if (cmd.ExitStatus != 0)
            throw new InvalidOperationException($"{stage}: код {cmd.ExitStatus}. {cmd.Error.Trim()} {output}");
        if (output.Length > 0) Logger.Information("{Output}", output);
    }
}
