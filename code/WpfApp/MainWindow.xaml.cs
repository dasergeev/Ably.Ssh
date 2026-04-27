using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace WpfApp;

public partial class MainWindow : Window
{
    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readerCts;

    public MainWindow()
    {
        InitializeComponent();
        UpdateUi(isConnected: false);

        AppLogger.EntryAdded += OnLogEntryAdded;
        foreach (var entry in AppLogger.GetSnapshot())
        {
            AppendJournalEntry(entry);
        }

        AppLogger.Info("Приложение запущено.");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sshClient?.IsConnected == true)
        {
            return;
        }

        var host = HostTextBox.Text.Trim();
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;

        if (!IPAddress.TryParse(host, out _))
        {
            AppLogger.Warn("Некорректный IP-адрес.");
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            AppLogger.Warn("Порт должен быть в диапазоне 1..65535.");
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            AppLogger.Warn("Имя пользователя не указано.");
            return;
        }

        SetStatus("Подключение...");
        AppLogger.Info($"Подключение к {host}:{port} от имени '{username}'.");

        try
        {
            var probeResult = await ProbeEndpointAsync(host, port);
            if (!probeResult.CanProceed)
            {
                SetStatus("Ошибка подключения");
                AppLogger.Error(probeResult.Message);
                return;
            }

            if (!string.IsNullOrWhiteSpace(probeResult.Message))
            {
                AppLogger.Info(probeResult.Message);
            }

            var connection = await Task.Run(() =>
            {
                var authentication = new PasswordAuthenticationMethod(username, password);
                var connectionInfo = new ConnectionInfo(host, port, username, authentication)
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };

                var client = new SshClient(connectionInfo);
                client.Connect();

                var shell = client.CreateShellStream("xterm", 120, 40, 800, 600, 2048);
                return (client, shell);
            });

            _sshClient = connection.client;
            _shellStream = connection.shell;

            _readerCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadShellOutputAsync(_readerCts.Token));

            AppendTerminalOutput($"[local] Подключено к {host}:{port}{Environment.NewLine}");
            SetStatus("Подключено");
            UpdateUi(isConnected: true);
            AppLogger.Info("SSH-сессия установлена.");
            CommandTextBox.Focus();
        }
        catch (Exception ex)
        {
            DisconnectInternal();
            SetStatus("Ошибка подключения");
            UpdateUi(isConnected: false);
            AppLogger.Error(GetConnectionHint(ex, host, port));
            AppLogger.Exception("SSH connection failed.", ex);
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectInternal();
        AppendTerminalOutput($"[local] Отключено{Environment.NewLine}");
        SetStatus("Отключено");
        UpdateUi(isConnected: false);
        AppLogger.Info("SSH-сессия закрыта.");
    }

    private void SendCommandButton_Click(object sender, RoutedEventArgs e)
    {
        SendCommand();
    }

    private void CommandTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendCommand();
        }
    }

    private void SendCommand()
    {
        var command = CommandTextBox.Text;
        if (string.IsNullOrWhiteSpace(command) || _shellStream is null || _sshClient?.IsConnected != true)
        {
            return;
        }

        try
        {
            _shellStream.WriteLine(command);
            AppendTerminalOutput($"> {command}{Environment.NewLine}");
            AppLogger.Info($"Команда отправлена: {command}");
            CommandTextBox.Clear();
        }
        catch (Exception ex)
        {
            AppLogger.Exception("Command send failed.", ex);
        }
    }

    private async Task ReadShellOutputAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_shellStream?.DataAvailable == true)
                {
                    var text = _shellStream.Read();
                    if (!string.IsNullOrEmpty(text))
                    {
                        await Dispatcher.InvokeAsync(() => AppendTerminalOutput(text));
                    }
                }
                else
                {
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Ошибка чтения SSH-потока.", ex);
                break;
            }
        }
    }

    private void DisconnectInternal()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;

        if (_sshClient is not null)
        {
            try
            {
                if (_sshClient.IsConnected)
                {
                    _sshClient.Disconnect();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Ошибка при отключении SSH-клиента.", ex);
            }

            _sshClient.Dispose();
            _sshClient = null;
        }

        _shellStream = null;
    }

    private void UpdateUi(bool isConnected)
    {
        HostTextBox.IsEnabled = !isConnected;
        PortTextBox.IsEnabled = !isConnected;
        UsernameTextBox.IsEnabled = !isConnected;
        PasswordBox.IsEnabled = !isConnected;
        ConnectButton.IsEnabled = !isConnected;
        DisconnectButton.IsEnabled = isConnected;
        SendCommandButton.IsEnabled = isConnected;
        CommandTextBox.IsEnabled = isConnected;
    }

    private void SetStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    private void AppendTerminalOutput(string text)
    {
        TerminalOutputTextBox.AppendText(text);
        TerminalOutputTextBox.ScrollToEnd();
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        if (Dispatcher.CheckAccess())
        {
            AppendJournalEntry(entry);
            return;
        }

        _ = Dispatcher.InvokeAsync(() => AppendJournalEntry(entry));
    }

    private void AppendJournalEntry(LogEntry entry)
    {
        var text = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}{Environment.NewLine}";
        JournalTextBox.AppendText(text);
        JournalTextBox.ScrollToEnd();
    }

    private static string GetConnectionHint(Exception exception, string host, int port)
    {
        if (exception is SshConnectionException sshEx)
        {
            if (sshEx.Message.Contains("does not contain an SSH identification string", StringComparison.OrdinalIgnoreCase))
            {
                if (sshEx.Message.Contains("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    return $"На {host}:{port} отвечает HTTP(S), а не SSH. Для Proxmox SSH обычно доступен на порту 22.";
                }

                return $"На {host}:{port} не обнаружен SSH-сервер. Проверь адрес/порт и что SSH-служба запущена.";
            }

            return $"Ошибка SSH-подключения: {sshEx.Message}";
        }

        return $"Ошибка подключения: {exception.Message}";
    }

    private static async Task<ProbeResult> ProbeEndpointAsync(string host, int port)
    {
        try
        {
            using var tcpClient = new TcpClient();

            using (var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                await tcpClient.ConnectAsync(host, port, connectTimeoutCts.Token);
            }

            using var stream = tcpClient.GetStream();
            var buffer = new byte[512];
            var read = 0;

            using (var readTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return new ProbeResult(
                        CanProceed: true,
                        Message: $"Предпроверка {host}:{port}: TCP открыт, SSH-баннер не получен за 2 сек. Продолжаем попытку SSH.");
                }
            }

            if (read <= 0)
            {
                return new ProbeResult(
                    CanProceed: true,
                    Message: $"Предпроверка {host}:{port}: TCP открыт, удалённый узел закрыл соединение без баннера. Продолжаем попытку SSH.");
            }

            var preview = Encoding.ASCII.GetString(buffer, 0, read);
            var firstLine = preview.Split(["\r\n", "\n"], StringSplitOptions.None)[0];

            if (preview.Contains("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeResult(
                    CanProceed: false,
                    Message: $"Предпроверка {host}:{port}: получен HTTP-ответ ({firstLine}). Это не SSH-порт.");
            }

            if (preview.Contains("SSH-", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeResult(
                    CanProceed: true,
                    Message: $"Предпроверка {host}:{port}: получен SSH-баннер ({firstLine}).");
            }

            return new ProbeResult(
                CanProceed: true,
                Message: $"Предпроверка {host}:{port}: нестандартный ответ ({firstLine}). Продолжаем попытку SSH.");
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(
                CanProceed: false,
                Message: $"Предпроверка {host}:{port}: таймаут TCP-подключения.");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(
                CanProceed: false,
                Message: $"Предпроверка {host}:{port}: ошибка сокета ({ex.SocketErrorCode}) - {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ProbeResult(
                CanProceed: false,
                Message: $"Предпроверка {host}:{port}: ошибка проверки - {ex.Message}");
        }
    }

    private sealed record ProbeResult(bool CanProceed, string Message);

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.EntryAdded -= OnLogEntryAdded;
        DisconnectInternal();
        base.OnClosed(e);
    }
}
