// MainWindow.xaml.cs
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
using System.Windows.Input;

namespace ChatClient
{
    public partial class MainWindow : Window
    {
        private TcpClient? client;
        private NetworkStream? stream;
        private bool isConnected = false;
        private string? privateMessageTargetUID = null;
        private string? privateMessageTargetUsername = null;
        // Menggunakan Dictionary untuk menyimpan user online: <UID, Username>
        private Dictionary<string, string> onlineUsers = new Dictionary<string, string>();

        public MainWindow()
        {
            InitializeComponent();
            txtUsername.Text = "";
            txtUsername.Focus();
        }

        public Message CreateMessage(string type, string text, string to = "")
        {
            // Klien hanya mengirimkan username. UID akan di-assign oleh server.
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

            if (string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                MessageBox.Show("Please enter a username before connecting.", "Username Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtUsername.Focus();
                return;
            }

            if (txtUsername.Text.Contains(":") || txtUsername.Text.Contains("/") || txtUsername.Text.Length > 20)
            {
                MessageBox.Show("Username cannot contain ':' or '/' and must be less than 20 characters.", "Invalid Username", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private async void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
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
            txtMessage.Focus();

            Message message;
            // Jika ada target PM, buat pesan tipe "pmsg" dengan tujuan UID target
            if (privateMessageTargetUID != null)
            {
                message = CreateMessage("pmsg", messageText, to: privateMessageTargetUID);
            }
            else // Jika tidak, buat pesan "msg" biasa (publik)
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
                    sb.Clear();
                    sb.Append(all);
                }
                catch (Exception)
                {
                    if (isConnected)
                    {
                        Dispatcher.Invoke(() => Disconnect());
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
                    onlineUsers = JsonSerializer.Deserialize<Dictionary<string, string>>(userListJson) ?? new Dictionary<string, string>();
                }

                lstOnlineUsers.Items.Clear();

                // Ambil semua username (Values), urutkan, lalu tampilkan
                var usersToShow = onlineUsers.Values
                    .Where(u => !string.IsNullOrEmpty(u))
                    .OrderBy(u => u)
                    .ToList();

                foreach (var user in usersToShow)
                {
                    lstOnlineUsers.Items.Add(user);
                }

                // Judul window sekarang menampilkan jumlah koneksi unik
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
                case "join": // Pesan join/leave tidak lagi ditampilkan secara manual,
                case "pmsg": // TAMBAHKAN CASE BARU INI
                    // Pesan akan di-echo kembali oleh server, jadi kita cek pengirimnya
                    if (message.From == txtUsername.Text) // Jika saya yang mengirim
                    {
                        // Cari username penerima dari dictionary `onlineUsers`
                        string toUsername = onlineUsers.TryGetValue(message.To, out var name) ? name : "Unknown";
                        displayText += $"[Private to {toUsername}]: {message.Text}";
                    }
                    else // Jika saya yang menerima
                    {
                        displayText += $"[Private from {message.From}]: {message.Text}";
                    }
                    break;
                case "leave":// server mengirimkannya sebagai pesan 'sys'
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
        
        // TAMBAHKAN METODE BARU INI
        private void lstOnlineUsers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstOnlineUsers.SelectedItem == null) return;

            string selectedUsername = lstOnlineUsers.SelectedItem.ToString()!;

            // Cari UID berdasarkan username yang dipilih
            var targetUserEntry = onlineUsers.FirstOrDefault(kvp => kvp.Value == selectedUsername);

            // Jika tidak ditemukan, jangan lakukan apa-apa
            if (string.IsNullOrEmpty(targetUserEntry.Key)) return;

            // Jika double-click orang yang sama, batalkan mode PM
            if (targetUserEntry.Key == privateMessageTargetUID)
            {
                privateMessageTargetUID = null;
                privateMessageTargetUsername = null;
                lblPmTarget.Visibility = Visibility.Collapsed;
                txtMessage.Focus();
            }
            else // Jika memilih orang baru, masuk ke mode PM
            {
                privateMessageTargetUID = targetUserEntry.Key;
                privateMessageTargetUsername = targetUserEntry.Value;
                lblPmTarget.Text = $"> {privateMessageTargetUsername}:";
                lblPmTarget.Visibility = Visibility.Visible;
                txtMessage.Focus();
            }
        }
    }

    // Definisi class Message agar file ini bisa berdiri sendiri.
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