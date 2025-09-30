// Program.cs
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

namespace ChatServer
{
    public class Message
    {
        public string Type { get; set; } = "";
        public string FromUID { get; set; } = ""; // ID Unik Pengirim
        public string From { get; set; } = "";    // Username Pengirim
        public string To { get; set; } = "";
        public string Text { get; set; } = "";
        public long Ts { get; set; }
    }

    public class ClientHandler
    {
        public TcpClient Client { get; set; } = null!;
        public string UID { get; set; } = ""; // Properti untuk menyimpan ID Unik
        public string Username { get; set; } = "";
        public NetworkStream Stream { get; set; } = null!;
    }

    class Program
    {
        private static List<ClientHandler> clients = new List<ClientHandler>();
        private static TcpListener server = null!;
        private static readonly object clientsLock = new object();

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Chat Server...");
            server = new TcpListener(IPAddress.Any, 8080);
            server.Start();
            Console.WriteLine("Server started on port 8080");

            try
            {
                while (true)
                {
                    var client = await server.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                server.Stop();
            }
        }

        static async Task HandleClientAsync(TcpClient client)
        {
            ClientHandler? handler = null;
            try
            {
                handler = new ClientHandler
                {
                    Client = client,
                    Stream = client.GetStream(),
                    UID = UIDGenerator.Generate() // Langsung buat UID saat koneksi dibuat
                };

                byte[] buffer = new byte[4096];
                int bytesRead;

                while ((bytesRead = await handler.Stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var parts = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var message = JsonSerializer.Deserialize<Message>(part);
                        if (message == null) continue;

                        switch (message.Type)
                        {
                            case "join":
                                handler.Username = message.From;
                                lock (clientsLock)
                                {
                                    clients.Add(handler);
                                }
                                Console.WriteLine($"{handler.Username} ({handler.UID}) connected");

                                await BroadcastSystemMessage($"{handler.Username} joined the chat");
                                await BroadcastUserList();
                                break;

                            case "msg":
                                // Selalu gunakan info dari sisi server untuk keamanan
                                message.FromUID = handler.UID;
                                message.From = handler.Username;
                                await BroadcastMessage(message);
                                break;

                            case "pmsg": // TAMBAHKAN CASE BARU INI
                                // Info pengirim diambil dari sisi server untuk keamanan
                                message.FromUID = handler.UID;
                                message.From = handler.Username;
                                await SendPrivateMessageAsync(message);
                                break;

                            case "leave":
                                Console.WriteLine($"{handler.Username} ({handler.UID}) requested leave");
                                // Proses disconnect akan ditangani oleh blok 'finally'
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Error biasanya terjadi saat klien disconnect paksa, ini normal.
            }
            finally
            {
                if (handler != null)
                {
                    lock (clientsLock)
                    {
                        clients.Remove(handler);
                    }
                    if (!string.IsNullOrEmpty(handler.Username))
                    {
                        Console.WriteLine($"{handler.Username} ({handler.UID}) disconnected.");
                        await BroadcastSystemMessage($"{handler.Username} left the chat");
                    }
                    await BroadcastUserList();

                    try
                    {
                        handler.Stream?.Close();
                        handler.Client?.Close();
                    }
                    catch { }
                }
            }
        }

        static async Task BroadcastMessage(Message message)
        {
            message.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string json = JsonSerializer.Serialize(message) + "\n";
            byte[] data = Encoding.UTF8.GetBytes(json);

            List<ClientHandler> currentClients;
            lock (clientsLock)
            {
                currentClients = clients.ToList();
            }

            foreach (var client in currentClients)
            {
                try
                {
                    if (client.Stream != null && client.Stream.CanWrite)
                    {
                        await client.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch
                {
                    // Error saat mengirim akan ditangani oleh loop utama di HandleClientAsync
                }
            }
        }

        static async Task BroadcastSystemMessage(string text)
        {
            var systemMessage = new Message
            {
                Type = "sys",
                From = "System",
                Text = text
            };
            await BroadcastMessage(systemMessage);
        }

        static async Task BroadcastUserList()
        {
            var userListPayload = GetOnlineUsers();
            var userListMessage = new Message
            {
                Type = "userlist",
                From = "Server",
                Text = JsonSerializer.Serialize(userListPayload)
            };
            await BroadcastMessage(userListMessage);
        }

        static Dictionary<string, string> GetOnlineUsers()
        {
            lock (clientsLock)
            {
                return clients
                    .Where(c => !string.IsNullOrEmpty(c.Username))
                    .ToDictionary(c => c.UID, c => c.Username);
            }
        }
        
        static async Task SendPrivateMessageAsync(Message message)
        {
            string recipientUID = message.To;
            string senderUID = message.FromUID;
            message.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            ClientHandler? recipient = null;
            ClientHandler? sender = null;

            lock (clientsLock)
            {
                recipient = clients.FirstOrDefault(c => c.UID == recipientUID);
                sender = clients.FirstOrDefault(c => c.UID == senderUID);
            }

            // Pastikan penerima dan pengirim ada (masih online)
            if (recipient != null && sender != null)
            {
                string json = JsonSerializer.Serialize(message) + "\n";
                byte[] data = Encoding.UTF8.GetBytes(json);

                try
                {
                    // 1. Kirim pesan ke penerima
                    if (recipient.Stream.CanWrite)
                    {
                        await recipient.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch { /* Penerima mungkin disconnect saat pesan dikirim */ }

                try
                {
                    // 2. Kirim "echo" pesan kembali ke pengirim agar muncul di chatbox mereka
                    if (sender.Stream.CanWrite)
                    {
                        await sender.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch { /* Pengirim mungkin disconnect */ }
            }
            else
            {
                // Opsional: kirim pesan error ke pengirim jika target tidak ditemukan
                // Untuk saat ini, kita abaikan saja jika target offline.
            }
        }
    }
}