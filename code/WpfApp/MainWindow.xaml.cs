using System.Net;
using System.Windows;
using System.Windows.Input;
using Renci.SshNet;

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

        AppLogger.Info("Application started.");
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
            AppLogger.Warn("Validation failed: invalid IP address.");
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            AppLogger.Warn("Validation failed: port must be in range 1..65535.");
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            AppLogger.Warn("Validation failed: user name is empty.");
            return;
        }

        SetStatus("Connecting...");
        ConnectButton.IsEnabled = false;
        AppLogger.Info($"Connecting to {host}:{port} as '{username}'.");

        try
        {
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

            AppendTerminalOutput($"[local] Connected to {host}:{port}{Environment.NewLine}");
            SetStatus("Connected");
            UpdateUi(isConnected: true);
            AppLogger.Info("SSH session established.");
            CommandTextBox.Focus();
        }
        catch (Exception ex)
        {
            DisconnectInternal();
            SetStatus("Connection failed");
            UpdateUi(isConnected: false);
            AppLogger.Exception("SSH connection failed.", ex);
        }
        finally
        {
            ConnectButton.IsEnabled = _sshClient?.IsConnected != true;
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectInternal();
        AppendTerminalOutput($"[local] Disconnected{Environment.NewLine}");
        SetStatus("Disconnected");
        UpdateUi(isConnected: false);
        AppLogger.Info("SSH session closed.");
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
            AppLogger.Info($"Command sent: {command}");
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
                        AppLogger.Info($"SSH output:{Environment.NewLine}{text.TrimEnd()}");
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
                AppLogger.Exception("SSH output reader failed.", ex);
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
                AppLogger.Exception("Error while disconnecting SSH client.", ex);
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

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.EntryAdded -= OnLogEntryAdded;
        DisconnectInternal();
        base.OnClosed(e);
    }
}
