// MainWindow.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private TcpClient? client;
        private NetworkStream? stream;
        private bool isConnected = false;
        private string? privateMessageTargetUID = null;
        private string? privateMessageTargetUsername = null;
        private Dictionary<string, string> onlineUsers = new Dictionary<string, string>();

        private DispatcherTimer typingTimer;
        private bool isTyping = false;
        private readonly Dictionary<string, string> typingUsers = new Dictionary<string, string>();
        private readonly Dictionary<string, DispatcherTimer> userTypingTimers = new Dictionary<string, DispatcherTimer>();

        public MainWindow()
        {
            InitializeComponent();
            txtUsername.Text = "";
            txtUsername.Focus();

            typingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            typingTimer.Tick += TypingTimer_Tick;
        }

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

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (isConnected) return;

            if (string.IsNullOrWhiteSpace(txtUsername.Text) || txtUsername.Text.Length > 20 || txtUsername.Text.Contains(":") || txtUsername.Text.Contains("/"))
            {
                MessageBox.Show("Username cannot be empty, contain ':' or '/', and must be less than 20 characters.", "Invalid Username", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show($"Connection failed: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateConnectionStatus(bool connected)
        {
            btnConnect.IsEnabled = !connected;
            btnDisconnect.IsEnabled = connected;
            txtMessage.IsEnabled = connected;
            btnSend.IsEnabled = connected;
            txtServerIP.IsEnabled = !connected;
            txtPort.IsEnabled = !connected;
            txtUsername.IsEnabled = !connected;

            txtStatus.Text = connected ? "Connected" : "Disconnected";
            var statusColorKey = connected ? "Text.Connected" : "Text.Disconnected";
            txtStatus.SetResourceReference(TextBlock.ForegroundProperty, statusColorKey);
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

        private async void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await SendChatMessage();
            }
        }

        private async Task SendChatMessage()
        {
            if (!isConnected || string.IsNullOrWhiteSpace(txtMessage.Text)) return;

            if (isTyping)
            {
                typingTimer.Stop();
                await SendTypingNotification(false);
                isTyping = false;
            }

            string messageText = txtMessage.Text.Trim();
            txtMessage.Clear();
            txtMessage.Focus();

            Message message;
            if (privateMessageTargetUID != null)
            {
                message = CreateMessage("pmsg", messageText, to: privateMessageTargetUID);
            }
            else
            {
                message = CreateMessage("msg", messageText);
            }

            await SendMessageAsync(message);
        }

        private async Task SendMessageAsync(Message message)
        {
            try
            {
                if (stream == null || !stream.CanWrite) return;

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
                        Dispatcher.Invoke(Disconnect);
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
                    sb.Clear();
                    sb.Append(all);
                }
                catch (Exception)
                {
                    if (isConnected) Dispatcher.Invoke(Disconnect);
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
                case "start_typing":
                    HandleUserTyping(message.FromUID, message.From, true);
                    break;
                case "stop_typing":
                    HandleUserTyping(message.FromUID, message.From, false);
                    break;
                default:
                    HandleUserTyping(message.FromUID, message.From, false);
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
                    onlineUsers = JsonSerializer.Deserialize<Dictionary<string, string>>(userListJson) ?? new Dictionary<string, string>();
                }

                lstOnlineUsers.Items.Clear();

                var usersToShow = onlineUsers.Values.Where(u => !string.IsNullOrEmpty(u)).OrderBy(u => u).ToList();

                foreach (var user in usersToShow)
                {
                    lstOnlineUsers.Items.Add(user);
                }

                this.Title = $"Chat Client - Online: {onlineUsers.Count} users";
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
                case "pmsg":
                    if (message.From == txtUsername.Text)
                    {
                        string toUsername = onlineUsers.TryGetValue(message.To, out var name) ? name : "Unknown";
                        displayText += $"[Private to {toUsername}]: {message.Text}";
                    }
                    else
                    {
                        displayText += $"[Private from {message.From}]: {message.Text}";
                    }
                    break;
                case "join":
                case "leave":
                    return;
                default:
                    displayText += $"{message.From}: {message.Text}";
                    break;
            }
            AddMessage(message.From, displayText);
        }

        private void AddMessage(string sender, string text)
        {
            lstMessages.Items.Add(text);
            if (lstMessages.Items.Count > 0)
            {
                lstMessages.ScrollIntoView(lstMessages.Items[lstMessages.Items.Count - 1]);
            }
        }

        private void lstOnlineUsers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstOnlineUsers.SelectedItem == null) return;
            string selectedUsername = lstOnlineUsers.SelectedItem.ToString()!;
            var targetUserEntry = onlineUsers.FirstOrDefault(kvp => kvp.Value == selectedUsername);

            if (string.IsNullOrEmpty(targetUserEntry.Key) || targetUserEntry.Value == txtUsername.Text) return;

            if (targetUserEntry.Key == privateMessageTargetUID)
            {
                privateMessageTargetUID = null;
                privateMessageTargetUsername = null;
                lblPmTarget.Visibility = Visibility.Collapsed;
            }
            else
            {
                privateMessageTargetUID = targetUserEntry.Key;
                privateMessageTargetUsername = targetUserEntry.Value;
                lblPmTarget.Text = $"> {privateMessageTargetUsername}:";
                lblPmTarget.Visibility = Visibility.Visible;
            }
            txtMessage.Focus();
        }

        public void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton)
            {
                ApplyTheme(toggleButton.IsChecked == true ? "DarkTheme" : "LightTheme");
            }
        }

        private void ApplyTheme(string themeName)
        {
            var existingDict = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme"));
            if (existingDict != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(existingDict);
            }
            var newDict = new ResourceDictionary { Source = new Uri($"Themes/{themeName}.xaml", UriKind.Relative) };
            Application.Current.Resources.MergedDictionaries.Add(newDict);
        }

        private async Task SendTypingNotification(bool isStarting)
        {
            var message = CreateMessage(isStarting ? "start_typing" : "stop_typing", "", to: privateMessageTargetUID ?? "");
            await SendMessageAsync(message);
        }

        private async void txtMessage_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!isConnected) return;
            typingTimer.Stop();

            if (!isTyping)
            {
                isTyping = true;
                await SendTypingNotification(true);
            }
            typingTimer.Start();
        }

        private async void TypingTimer_Tick(object? sender, EventArgs e)
        {
            typingTimer.Stop();
            isTyping = false;
            await SendTypingNotification(false);
        }

        private void HandleUserTyping(string uid, string username, bool isStarting)
        {
            if (string.IsNullOrEmpty(uid) || uid == "Server") return;

            if (isStarting)
            {
                typingUsers[uid] = username;
                if (userTypingTimers.TryGetValue(uid, out var timer))
                {
                    timer.Stop();
                }
                else
                {
                    timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    timer.Tick += (s, e) => {
                        HandleUserTyping(uid, username, false);
                        (s as DispatcherTimer)?.Stop();
                    };
                    userTypingTimers[uid] = timer;
                }
                timer.Start();
            }
            else
            {
                typingUsers.Remove(uid);
                if (userTypingTimers.TryGetValue(uid, out var timer))
                {
                    timer.Stop();
                    userTypingTimers.Remove(uid);
                }
            }
            UpdateTypingStatusLabel();
        }

        private void UpdateTypingStatusLabel()
        {
            var names = typingUsers.Values.ToList();
            string statusText = "";

            if (names.Count == 1) statusText = $"{names[0]} is typing...";
            else if (names.Count == 2) statusText = $"{names[0]} and {names[1]} are typing...";
            else if (names.Count > 2) statusText = "Several people are typing...";

            lblTypingStatus.Text = statusText;
        }
    }

    public class Message
    {
        public string Type { get; set; } = "";
        public string FromUID { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Text { get; set; } = "";
        public long Ts { get; set; }
    }
}