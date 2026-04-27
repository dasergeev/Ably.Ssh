using System.Net;
using System.Text;
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
            MessageBox.Show("Укажите корректный IP-адрес.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Порт должен быть числом от 1 до 65535.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("Укажите имя пользователя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus("Подключение...");
        ConnectButton.IsEnabled = false;

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

            AppendOutput($"[local] Подключено к {host}:{port}{Environment.NewLine}");
            SetStatus("Подключено");
            UpdateUi(isConnected: true);
            CommandTextBox.Focus();
        }
        catch (Exception ex)
        {
            DisconnectInternal();
            SetStatus("Ошибка подключения");
            UpdateUi(isConnected: false);
            MessageBox.Show($"Не удалось подключиться: {ex.Message}", "SSH", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ConnectButton.IsEnabled = _sshClient?.IsConnected != true;
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectInternal();
        AppendOutput($"[local] Отключено{Environment.NewLine}");
        SetStatus("Отключено");
        UpdateUi(isConnected: false);
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
            AppendOutput($"> {command}{Environment.NewLine}");
            CommandTextBox.Clear();
        }
        catch (Exception ex)
        {
            AppendOutput($"[local] Ошибка отправки команды: {ex.Message}{Environment.NewLine}");
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
                        await Dispatcher.InvokeAsync(() => AppendOutput(text));
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
                await Dispatcher.InvokeAsync(() =>
                    AppendOutput($"[local] Ошибка чтения: {ex.Message}{Environment.NewLine}"));
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
            catch
            {
                // ignore
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

    private void AppendOutput(string text)
    {
        TerminalOutputTextBox.AppendText(text);
        TerminalOutputTextBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        DisconnectInternal();
        base.OnClosed(e);
    }
}
