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
        public string Type { get; set; } = ""; // msg, join, leave, sys, userlist
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Text { get; set; } = "";
        public long Ts { get; set; }
    }

    public class ClientHandler
    {
        public TcpClient Client { get; set; } = null!;
        public string Username { get; set; } = "";
        public NetworkStream Stream { get; set; } = null!;
    }

    class Program
    {
        private static List<ClientHandler> clients = new List<ClientHandler>();
        private static TcpListener server = null!;

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
                    Stream = client.GetStream() 
                };

                byte[] buffer = new byte[1024];
                int bytesRead;

                while ((bytesRead = await handler.Stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var message = JsonSerializer.Deserialize<Message>(json);

                    if (message == null) continue;

                    switch (message.Type)
                    {
                        case "join":
                            handler.Username = message.From;
                            clients.Add(handler);
                            Console.WriteLine($"{message.From} connected");
                            
                            // Broadcast ke semua client tentang user baru
                            await BroadcastSystemMessage($"{message.From} joined the chat");
                            // Update daftar user semua client
                            await BroadcastUserList();
                            break;

                        case "msg":
                            await BroadcastMessage(message);
                            break;
                        
                        case "leave":
                            if (!string.IsNullOrEmpty(handler.Username))
                            {
                                await BroadcastSystemMessage($"{handler.Username} left the chat");
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client handling error: {ex.Message}");
            }
            finally
            {
                if (handler != null)
                {
                    clients.Remove(handler);
                    if (!string.IsNullOrEmpty(handler.Username))
                    {
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
            
            string json = JsonSerializer.Serialize(message);
            byte[] data = Encoding.UTF8.GetBytes(json);

            var clientsToRemove = new List<ClientHandler>();

            foreach (var client in clients.ToList())
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
                    clientsToRemove.Add(client);
                }
            }

            foreach (var client in clientsToRemove)
            {
                clients.Remove(client);
            }
        }

        static async Task BroadcastSystemMessage(string text)
        {
            var systemMessage = new Message
            {
                Type = "sys",
                From = "System",
                Text = text,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await BroadcastMessage(systemMessage);
        }

        static async Task SendUserListToClient(ClientHandler clientHandler)
        {
            var userList = GetOnlineUsernames();
            var userListMessage = new Message
            {
                Type = "userlist",
                From = "Server",
                Text = JsonSerializer.Serialize(userList),
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            string json = JsonSerializer.Serialize(userListMessage);
            byte[] data = Encoding.UTF8.GetBytes(json);

            if (clientHandler.Stream != null && clientHandler.Stream.CanWrite)
            {
                await clientHandler.Stream.WriteAsync(data, 0, data.Length);
            }
        }

        static async Task BroadcastUserList()
        {
            var userList = GetOnlineUsernames();
            var userListMessage = new Message
            {
                Type = "userlist",
                From = "Server",
                Text = JsonSerializer.Serialize(userList),
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await BroadcastMessage(userListMessage);
        }

        static List<string> GetOnlineUsernames()
        {
            return clients
                .Where(c => !string.IsNullOrEmpty(c.Username))
                .Select(c => c.Username)
                .Distinct()
                .ToList();
        }
    }
}