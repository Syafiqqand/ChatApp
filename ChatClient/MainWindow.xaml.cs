using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private TcpClient? client;
        private NetworkStream? stream;
        private bool isConnected = false;
        private List<string> onlineUsers = new List<string>();

        public Message CreateMessage(string type, string text, string to = "")
        {
            return new Message
            {
                Type = type,
                From = txtUsername.Text,
                To = to,
                Text = text,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        public MainWindow()
        {
            InitializeComponent();
            txtUsername.Text = "";
            txtUsername.Focus();
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected) return;

            if (string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                MessageBox.Show("Please enter a username before connecting.", "Username Required",
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                txtUsername.Focus();
                return;
            }

            if (txtUsername.Text.Contains(":") || txtUsername.Text.Contains("/") || txtUsername.Text.Length > 20)
            {
                MessageBox.Show("Username cannot contain ':' or '/' and must be less than 20 characters.",
                              "Invalid Username", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtUsername.Focus();
                return;
            }

            try
            {
                client = new TcpClient();
                await client.ConnectAsync(txtServerIP.Text, int.Parse(txtPort.Text));
                stream = client.GetStream();

                isConnected = true;
                UpdateConnectionStatus(true);
                _ = Task.Run(ReceiveMessagesAsync);

                var joinMsg = CreateMessage("join", $"{txtUsername.Text} joined the chat");
                await SendMessageAsync(joinMsg);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection failed: {ex.Message}", "Connection Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateConnectionStatus(bool connected)
        {
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
            txtMessage.IsEnabled = connected;
            btnSend.IsEnabled = connected;
            txtStatus.Text = connected ? "Connected" : "Disconnected";
            txtStatus.Foreground = connected ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red;
        }

        private async void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (!isConnected) return;

            try
            {
                var leaveMsg = CreateMessage("leave", $"{txtUsername.Text} left the chat");
                await SendMessageAsync(leaveMsg);
            }
            catch { }

            Disconnect();
        }

        private void Disconnect()
        {
            isConnected = false;

            try
            {
                stream?.Close();
                client?.Close();
            }
            catch { }

            UpdateConnectionStatus(false);

            onlineUsers.Clear();
            UpdateOnlineUsersList();
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendChatMessage();
        }

        private async void txtMessage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                await SendChatMessage();
            }
        }

        private async Task SendChatMessage()
        {
            if (!isConnected || string.IsNullOrWhiteSpace(txtMessage.Text))
                return;

            string messageText = txtMessage.Text.Trim();
            txtMessage.Clear();

            Message message = CreateMessage("msg", messageText);

            await SendMessageAsync(message);
        }

        private async Task SendMessageAsync(Message message)
        {
            try
            {
                if (stream == null) return;

                string json = JsonSerializer.Serialize(message) + "\n"; 
                byte[] data = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AddMessage("System", $"Send error: {ex.Message}");
                    Disconnect();
                });
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            if (stream == null) return;

            byte[] buffer = new byte[4096];
            var sb = new StringBuilder();

            while (isConnected && client != null && client.Connected)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        Dispatcher.Invoke(() => Disconnect());
                        break;
                    }

                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    sb.Append(chunk);

                    string all = sb.ToString();
                    int newlineIndex;
                    while ((newlineIndex = all.IndexOf('\n')) != -1)
                    {
                        string line = all.Substring(0, newlineIndex).Trim();
                        if (!string.IsNullOrEmpty(line))
                        {
                            try
                            {
                                var message = JsonSerializer.Deserialize<Message>(line);
                                if (message != null)
                                {
                                    Dispatcher.Invoke(() => ProcessMessage(message));
                                }
                            }
                            catch (JsonException) { /* skip invalid json fragment */ }
                        }
                        all = all.Substring(newlineIndex + 1);
                    }

                    // keep leftover partial
                    sb.Clear();
                    sb.Append(all);
                }
                catch (Exception ex)
                {
                    if (isConnected)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AddMessage("System", $"Connection error: {ex.Message}");
                            Disconnect();
                        });
                    }
                    break;
                }
            }
        }

        private void ProcessMessage(Message message)
        {
            switch (message.Type)
            {
                case "userlist":
                    UpdateOnlineUsersList(message.Text);
                    break;
                default:
                    DisplayMessage(message);
                    break;
            }
        }

        private void UpdateOnlineUsersList(string userListJson = "")
        {
            try
            {
                if (!string.IsNullOrEmpty(userListJson))
                {
                    onlineUsers = JsonSerializer.Deserialize<List<string>>(userListJson) ?? new List<string>();
                }

                lstOnlineUsers.Items.Clear();

                var usersToShow = onlineUsers
                    .Where(u => !string.IsNullOrEmpty(u))
                    .OrderBy(u => u)
                    .ToList();

                foreach (var user in usersToShow)
                {
                    lstOnlineUsers.Items.Add(user);
                }

                this.Title = $"Chat Client - Online: {usersToShow.Count} users";
            }
            catch (Exception ex)
            {
                AddMessage("System", $"Error updating user list: {ex.Message}");
            }
        }

        private void DisplayMessage(Message message)
        {
            string displayText = $"[{DateTime.Now:HH:mm}] ";

            switch (message.Type)
            {
                case "sys":
                    displayText += $"SYSTEM: {message.Text}";
                    break;
                case "join":
                    displayText += $"{message.From} joined";
                    break;
                case "leave":
                    displayText += $"{message.From} left";
                    break;
                default:
                    displayText += $"{message.From}: {message.Text}";
                    break;
            }

            AddMessage(message.From, displayText);
        }

        private void AddMessage(string sender, string text)
        {
            lstMessages.Items.Add(text);
            lstMessages.ScrollIntoView(lstMessages.Items[lstMessages.Items.Count - 1]);
        }
    }

    public class Message
    {
        public string Type { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Text { get; set; } = "";
        public long Ts { get; set; }
    }
}
