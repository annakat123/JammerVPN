using Avalonia;
using System;

namespace Jammer_desktop;

public static class AppText
{
    public static event Action? Changed;
    public static void Apply(string language)
    {
        foreach (var (key, ru, en) in new[]
        {
            ("General", "Общее", "General"),
            ("Connect", "Подключиться", "Connect"),
            ("Connecting", "Подключение…", "Connecting…"),
            ("Disconnect", "Отключиться", "Disconnect"),
            ("InvalidIp", "Введите корректный IPv4-адрес", "Enter a valid IPv4 address"),
            ("AdminRequired", "Для подключения запустите приложение от имени администратора", "Run the application as administrator to connect"),
            ("Logs", "Журнал", "Logs"),
            ("About", "О Jammer Protocol", "About Jammer Protocol"),
            ("ServerIp", "IP-адрес сервера", "Server IP address"),
            ("SettingsError", "Не удалось сохранить настройки", "Could not save settings"),
            ("ConsoleHint", "Здесь появится вывод приложения", "Application output will appear here"),
            ("Startup", "Запускать вместе с Windows", "Start with Windows"),
            ("StartupHint", "Автозапуск при входе в систему", "Launch when signing in"),
            ("Tray", "Сворачивать в трей", "Minimize to tray"),
            ("TrayHint", "Закрытие окна не отключает VPN", "Closing the window keeps VPN connected"),
            ("Logging", "Логирование", "Logging"),
            ("LoggingHint", "Подробная диагностика", "Detailed diagnostics"),
            ("LoggingHelp", "Выключено: основные события и ошибки. Включено: подробная диагностика.", "Off: connection events and errors. On: detailed diagnostics."),
            ("ChooseLogFolder", "Выбрать папку журналов", "Choose log folder"),
            ("LogFolderError", "Не удалось записать в выбранную папку", "Cannot write to the selected folder"),
            ("Language", "Язык", "Language"),
            ("LanguageHint", "Язык работы приложения", "Application language"),
            ("Login", "Логин", "Username"),
            ("Password", "Пароль", "Password"),
            ("Install", "Установить", "Install"),
            ("InstallHint", "Здесь появится ход установки", "Installation progress will appear here"),
            ("OpenLog", "Открыть журнал", "Open log"),
            ("NoLogs", "Файл журнала ещё не создан", "No log file has been created yet"),
            ("OpenLogError", "Не удалось открыть журнал", "Could not open log")
        })
            Application.Current!.Resources[key] = language == "en" ? en : ru;
        Changed?.Invoke();
    }

    public static string Get(string key) => (string)Application.Current!.Resources[key]!;
}
