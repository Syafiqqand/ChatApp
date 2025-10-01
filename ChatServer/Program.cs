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
    //menentukan struktur atau kerangka data untuk setiap pesan yang dikirim
    public class Message
    {
        public string Type { get; set; } = "";
        public string FromUID { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Text { get; set; } = "";
        public long Ts { get; set; }
    }

    //menyimpan semua informasi tentang satu client yang terhubung
    public class ClientHandler
    {
        public TcpClient Client { get; set; } = null!;
        public string UID { get; set; } = "";
        public string Username { get; set; } = "";
        public NetworkStream Stream { get; set; } = null!;
    }

    class Program
    {
        private static List<ClientHandler> clients = new List<ClientHandler>();
        private static TcpListener server = null!;
        private static readonly object clientsLock = new object();

        //fungsi utama yang pertama kali dijalankan saat server dinyalakan
        //fungsinya memulai server dan terus-menerus menunggu koneksi baru dari client
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

        //mengurus semua komunikasi untuk satu client mulai dari menerima pesan hingga saat client disconnect
        static async Task HandleClientAsync(TcpClient client)
        {
            ClientHandler? handler = null;
            try
            {
                handler = new ClientHandler
                {
                    Client = client,
                    Stream = client.GetStream(),
                    UID = UIDGenerator.Generate() // Memanggil dari file UIDHandler.cs Anda
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

                        if (message.Type == "join")
                        {
                            handler.Username = message.From;
                            lock (clientsLock)
                            {
                                clients.Add(handler);
                            }
                            Console.WriteLine($"{handler.Username} ({handler.UID}) connected");

                            message.FromUID = handler.UID;

                            await BroadcastSystemMessage($"{handler.Username} joined the chat");
                            await BroadcastUserList();
                            continue;
                        }

                        message.FromUID = handler.UID;
                        message.From = handler.Username;

                        switch (message.Type)
                        {
                            case "msg":
                                await BroadcastMessage(message);
                                break;

                            case "pmsg":
                                await SendPrivateMessageAsync(message);
                                break;

                            case "start_typing":
                            case "stop_typing":
                                if (string.IsNullOrEmpty(message.To))
                                {
                                    await BroadcastToOthersAsync(message);
                                }
                                else
                                {
                                    await ForwardMessageAsync(message);
                                }
                                break;

                            case "leave":
                                Console.WriteLine($"{handler.Username} ({handler.UID}) requested leave");
                                break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                //trjadi error saat klien disconnect paksa
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

        //mengirimkan pesan ke semua client
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

        //untuk menyiarkan pesan dari sistem
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

        //mengirim semua user yg online ke client
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

        //mengambil data dari semua client
        static Dictionary<string, string> GetOnlineUsers()
        {
            lock (clientsLock)
            {
                return clients
                    .Where(c => !string.IsNullOrEmpty(c.Username))
                    .ToDictionary(c => c.UID, c => c.Username);
            }
        }

        //fungsi untuk private message
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

            if (recipient != null && sender != null)
            {
                string json = JsonSerializer.Serialize(message) + "\n";
                byte[] data = Encoding.UTF8.GetBytes(json);

                try
                {
                    if (recipient.Stream.CanWrite)
                    {
                        await recipient.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch { /* Penerima mungkin disconnect saat pesan dikirim */ }

                try
                {
                    if (sender.Stream.CanWrite)
                    {
                        await sender.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch { /* Pengirim mungkin disconnect */ }
            }
        }

        //mengirim pesan seperti notif typing ke 1 client tertentu (PM)
        static async Task ForwardMessageAsync(Message message)
        {
            ClientHandler? recipient;
            lock (clientsLock)
            {
                recipient = clients.FirstOrDefault(c => c.UID == message.To);
            }

            if (recipient != null)
            {
                try
                {
                    if (recipient.Stream.CanWrite)
                    {
                        string json = JsonSerializer.Serialize(message) + "\n";
                        byte[] data = Encoding.UTF8.GetBytes(json);
                        await recipient.Stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch { /* Abaikan jika gagal mengirim ke target */ }
            }
        }

        //fungsi ini mengirim pesan ke semua client kecuali si pengirim asli
        static async Task BroadcastToOthersAsync(Message message)
        {
            string json = JsonSerializer.Serialize(message) + "\n";
            byte[] data = Encoding.UTF8.GetBytes(json);

            List<ClientHandler> currentClients;
            lock (clientsLock)
            {
                currentClients = clients.ToList();
            }

            foreach (var client in currentClients)
            {
                if (client.UID != message.FromUID)
                {
                    try
                    {
                        if (client.Stream.CanWrite)
                        {
                            await client.Stream.WriteAsync(data, 0, data.Length);
                        }
                    }
                    catch { /* Abaikan jika gagal mengirim */ }
                }
            }
        }
    }
}