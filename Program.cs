//// See https://aka.ms/new-console-template for more information
//Console.WriteLine("Hello, World!");

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;


public class GameServer
{
    private static readonly ConcurrentDictionary<string, Socket> Clients = new();
    private static readonly ConcurrentDictionary<Socket, int> ClientIds = new();
    private static TcpListener serverListener;
    private static int clientIdCounter = 1;
    private const int Port = 12345;
    private const int BufferSize = 1024;
    private const int maxClientCount = 30;
    private static readonly string LogFilePath = "server_log.txt"; // 日志文件路径

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += async (sender, e) =>
        {
            e.Cancel = true; // 防止程序直接退出
            await ShutdownServer();
        };

        await StartServer();
    }

    public static async Task StartServer()
    {
        try
        {
            serverListener = new TcpListener(IPAddress.Any, Port);
            serverListener.Start();

            string localIP = GetLocalIPAddress();
            Log($"服务器已启动，本机IP：{localIP}，监听端口 {Port}，等待客户端连接...");

            while (true)
            {
                TcpClient tcpClient = await serverListener.AcceptTcpClientAsync();
                Socket clientSocket = tcpClient.Client;

                if (Clients.Count >= maxClientCount)
                {
                    Log($"连接已达到最大限制，拒绝客户端: {clientSocket.RemoteEndPoint}");
                    byte[] rejectMessage = Encoding.UTF8.GetBytes("Server is full. Connection rejected.");
                    await clientSocket.SendAsync(new ArraySegment<byte>(rejectMessage), SocketFlags.None);
                    clientSocket.Close();
                    continue; // 不接受这个客户端，直接跳过
                }

                string clientKey = $"{clientSocket.RemoteEndPoint}___{clientIdCounter}";
                Clients[clientKey] = clientSocket;
                int clientId = clientIdCounter++;
                ClientIds[clientSocket] = clientId;

                Log($"客户端连接: {clientSocket.RemoteEndPoint}，客户端ID: {clientId}");


                byte[] idMessage = Encoding.UTF8.GetBytes(clientId.ToString());
                await clientSocket.SendAsync(new ArraySegment<byte>(idMessage), SocketFlags.None);
                Log($"已向 {clientSocket.RemoteEndPoint} 发送客户端ID: {clientId}");

                _ = Task.Run(() => HandleClient(clientSocket));
            }
        }
        catch (Exception ex)
        {
            Log($"服务器启动失败: {ex.Message}");
        }
    }

    public static async Task HandleClient(Socket clientSocket)
    {
        byte[] buffer = new byte[BufferSize];
        CircularBuffer dataBuffer = new CircularBuffer(BufferSize * 100); // 创建一个环形缓冲区

        var lastHeartbeat = DateTime.Now;  // 最后一次收到心跳包的时间
        var heartbeatInterval = TimeSpan.FromSeconds(10);  // 心跳包时间间隔


        try
        {
            while (true)
            {
                if ((DateTime.Now - lastHeartbeat) > heartbeatInterval)
                {
                    Log($"心跳超时， {ClientIds[clientSocket]}: {clientSocket.RemoteEndPoint} 断开连接 ... ");
                    await BroadcastClientDisconnect(clientSocket); // 客户端断开时广播
                    clientSocket.Close(); 
                    break;
                }


                // 接收数据
                int bytesReceived = await clientSocket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                if (bytesReceived == 0)
                {
                    var clientEndPoint = clientSocket.RemoteEndPoint;
                    var clientId = ClientIds[clientSocket];
                    Log($"客户端 {clientId}: {clientEndPoint} 主动断开连接");
                    await BroadcastClientDisconnect(clientSocket); // 客户端主动断开时广播
                    RemoveClient(clientSocket);
                    break;
                }

                // 将接收到的数据写入环形缓冲区
                dataBuffer.Write(buffer, 0, bytesReceived);

                // 尝试解析完整的数据包
                while (dataBuffer.Length >= 10) // 至少包含包头(6字节) + 数据长度(4字节)
                {
                    dataBuffer.Peek(0, 6, out byte[] header);
                    string headerStr = Encoding.UTF8.GetString(header);
                    if (headerStr != "HEADER")
                    {
                        Log("无效包头，丢弃数据");
                        dataBuffer.Discard(6);
                        continue;
                    }

                    // 读取数据长度
                    dataBuffer.Peek(6, 4, out byte[] lengthBytes);
                    int bodyLength = BitConverter.ToInt32(lengthBytes, 0);

                    if (dataBuffer.Length < 10 + bodyLength)
                    {
                        Log("数据包不完整，等待更多数据...");
                        break;
                    }

                    // 读取完整数据包
                    dataBuffer.Read(10 + bodyLength, out byte[] fullPacket);
                    byte[] body = fullPacket[10..]; // 提取消息体



                    // 如果消息是心跳包，更新最后一次收到心跳包的时间
                    if (Encoding.UTF8.GetString(body).Equals("HEARTBEAT"))
                    {
                        lastHeartbeat = DateTime.Now;  // 更新心跳包时间
                        //Log($"{ClientIds[clientSocket]}: {clientSocket.RemoteEndPoint} 收到心跳包，保持连接 ...");
                    }
                    else
                    {
                        // 处理其他类型的消息
                        await ProcessMessage(clientSocket, body);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"与客户端 {clientSocket.RemoteEndPoint} 通信时发生错误: {ex.Message}");
        }
        finally
        {
            RemoveClient(clientSocket);
        }
    }



    public static async Task ProcessMessage(Socket clientSocket, byte[] message)
    {
        if (TryParseNetworkMessage(message, out NetworkMessage networkMessage))
        {
            Log($"收到来自客户端 {clientSocket.RemoteEndPoint}___{ClientIds[clientSocket]} NetworkMessage: 类型={networkMessage.MessageType}");
            await BroadcastNetworkMessage(clientSocket, networkMessage);
        }
        else
        {
            string receivedMessage = Encoding.UTF8.GetString(message);
            Log($"收到来自客户端 {clientSocket.RemoteEndPoint}___{ClientIds[clientSocket]} 的消息: {receivedMessage}");
            await BroadcastMessage(clientSocket, receivedMessage);
        }
    }

    public static async Task BroadcastMessage(Socket senderSocket, string message)
    {
        byte[] combinedMessage = PrepareMessage(message);

        Log(" ------------------------广播开始------------------------");
        foreach (var kvp in Clients)
        {
            var clientKey = kvp.Key;
            var clientSocket = kvp.Value;

            try
            {
                if (clientSocket.Connected)
                {
                    await clientSocket.SendAsync(new ArraySegment<byte>(combinedMessage), SocketFlags.None);
                    Log($"广播消息给客户端: {clientKey}");
                }
                else
                {
                    Log($"检测到断开的客户端: {clientKey}");
                    Clients.TryRemove(clientKey, out _);
                    ClientIds.TryRemove(clientSocket, out _);
                }
            }
            catch (Exception ex)
            {
                Log($"广播消息失败: {ex.Message}");
                Clients.TryRemove(clientKey, out _);
                ClientIds.TryRemove(clientSocket, out _);
            }
        }
        Log(" ------------------------广播结束------------------------");
    }
    public static async Task BroadcastNetworkMessage(Socket senderSocket, NetworkMessage networkMessage)
    {
        byte[] combinedMessage = PrepareNetworkMessage(networkMessage);

        //Log(" ------------------------广播开始------------------------");
        var tasks = new List<Task>();
        foreach (var kvp in Clients)
        {
            var clientKey = kvp.Key;
            var clientSocket = kvp.Value;

            if (clientSocket.Connected)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await clientSocket.SendAsync(new ArraySegment<byte>(combinedMessage), SocketFlags.None);
                        Log($"广播消息给客户端: {clientKey}");
                    }
                    catch (Exception ex)
                    {
                        Log($"广播到客户端 {kvp.Key} 失败: {ex.Message}");
                        RemoveClient(clientSocket);
                    }
                }));
            }
        }

        await Task.WhenAll(tasks);
        Log(" ------------------------广播结束------------------------");
    }
    public static async Task BroadcastClientDisconnect(Socket disconnectedClientSocket)
    {
        string disconnectMessage = $"客户端 {disconnectedClientSocket.RemoteEndPoint}___{ClientIds[disconnectedClientSocket]}  已断开连接";
        byte[] messageBytes = Encoding.UTF8.GetBytes(disconnectMessage);
        byte[] combinedMessage = PrepareMessage(disconnectMessage); 

        Log(" ------------------------客户端断开------------------------");
        foreach (var kvp in Clients)
        {
            var clientSocket = kvp.Value;

            // 确保不要广播给自己
            if (clientSocket != disconnectedClientSocket)
            {
                try
                {
                    if (clientSocket.Connected)
                    {
                        await clientSocket.SendAsync(new ArraySegment<byte>(combinedMessage), SocketFlags.None);
                        Log($"广播客户端断开消息给客户端: {kvp.Key}");
                    }
                    else
                    {
                        Log($"检测到断开的客户端: {kvp.Key}");
                        Clients.TryRemove(kvp.Key, out _);
                        ClientIds.TryRemove(clientSocket, out _);
                    }
                }
                catch (Exception ex)
                {
                    Log($"广播消息失败: {ex.Message}");
                    Clients.TryRemove(kvp.Key, out _);
                    ClientIds.TryRemove(clientSocket, out _);
                }
            }
        }
        Log(" ------------------------客户端断开结束------------------------");
    }



    public static byte[] PrepareMessage(string message)
    {
        byte[] header = Encoding.UTF8.GetBytes("HEADER");
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);

        byte[] combinedMessage = new byte[header.Length + lengthBytes.Length + messageBytes.Length];
        Array.Copy(header, 0, combinedMessage, 0, header.Length);
        Array.Copy(lengthBytes, 0, combinedMessage, header.Length, lengthBytes.Length);
        Array.Copy(messageBytes, 0, combinedMessage, header.Length + lengthBytes.Length, messageBytes.Length);

        return combinedMessage;
    }

    public static byte[] PrepareNetworkMessage(NetworkMessage networkMessage)
    {
        byte[] header = Encoding.UTF8.GetBytes("HEADER");
        byte[] typeBytes = BitConverter.GetBytes((int)networkMessage.MessageType);
        byte[] dataBytes = networkMessage.Data;
        byte[] lengthBytes = BitConverter.GetBytes(typeBytes.Length + dataBytes.Length);

        byte[] combinedMessage = new byte[header.Length + lengthBytes.Length + typeBytes.Length + dataBytes.Length];
        Array.Copy(header, 0, combinedMessage, 0, header.Length);
        Array.Copy(lengthBytes, 0, combinedMessage, header.Length, lengthBytes.Length);
        Array.Copy(typeBytes, 0, combinedMessage, header.Length + lengthBytes.Length, typeBytes.Length);
        Array.Copy(dataBytes, 0, combinedMessage, header.Length + lengthBytes.Length + typeBytes.Length, dataBytes.Length);

        //Log($"PrepareNetworkMessage() - 包头长度: {header.Length}, 包体长度: {lengthBytes.Length}, 消息类型长度: {typeBytes.Length}, 数据长度: {dataBytes.Length}, 总长度: {combinedMessage.Length}");

        return combinedMessage;
    }



    private static void RemoveClient(Socket clientSocket)
    {
        try
        {
            if (ClientIds.TryRemove(clientSocket, out int clientId))
            {
                var clientKey = $"{clientSocket.RemoteEndPoint}___{clientId}";
                Clients.TryRemove(clientKey, out _);
                clientSocket.Dispose();
                Log($"客户端 {clientKey} 已断开连接并清理资源。");
            }
        }
        catch (Exception ex)
        {
            Log($"移除客户端时发生异常: {ex.Message}");
        }

        //var clientEndPoint = clientSocket.RemoteEndPoint; // 提前保存
        //var clientId = ClientIds[clientSocket];

        //var clientKey = $"{clientSocket.RemoteEndPoint}___{ClientIds[clientSocket]}";
        //if (Clients.ContainsKey(clientKey))
        //{
        //    Clients.TryRemove(clientKey, out _);
        //    ClientIds.TryRemove(clientSocket, out _);
        //    clientSocket.Dispose();
        //    Log($"客户端 {clientKey} 已断开连接并清理资源。");
        //}
    }

    public static async Task ShutdownServer()
    {
        foreach (var kvp in Clients)
        {
            var clientKey = kvp.Key;
            var clientSocket = kvp.Value;

            try
            {
                if (clientSocket.Connected)
                {
                    clientSocket.Shutdown(SocketShutdown.Both);
                    clientSocket.Close();
                    clientSocket.Dispose();
                    Log($"客户端 {clientKey} 已关闭。");
                }
            }
            catch (Exception ex)
            {
                Log($"关闭客户端 {clientKey} 失败: {ex.Message}");
            }
        }
        Clients.Clear(); // 清理所有客户端


        serverListener.Stop();
        Log("服务器已关闭。");
    }



    public static string GetLocalIPAddress()
    {
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }
        return "127.0.0.1";
    }



    public static void Log(string message)
    {
        try
        {
            Console.WriteLine($"[{DateTime.Now}] {message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"日志输出失败: {ex.Message}");
        }
    }


    public static bool TryParseNetworkMessage(byte[] message, out NetworkMessage networkMessage)
    {
        try
        {
            if (message.Length < 4)
            {
                networkMessage = null;
                return false;
            }

            NetworkMessageType messageType = (NetworkMessageType)BitConverter.ToInt32(message, 0);
            byte[] data = new byte[message.Length - 4];
            Array.Copy(message, 4, data, 0, data.Length);

            networkMessage = new NetworkMessage(messageType, data);
            return true;
        }
        catch
        {
            networkMessage = null;
            return false;
        }
    }
}

public class NetworkMessage
{
    public NetworkMessageType MessageType { get; }
    public byte[] Data { get; }

    public NetworkMessage(NetworkMessageType messageType, byte[] data)
    {
        MessageType = messageType;
        Data = data;
    }
}

public enum NetworkMessageType
{
    PositionUpdate,
    CharacterAction,
    ObjectSpawn
}


public class CircularBuffer
{
    private readonly byte[] buffer;
    private int head;
    private int tail;
    private int count;

    public CircularBuffer(int capacity)
    {
        buffer = new byte[capacity];
        head = 0;
        tail = 0;
        count = 0;
    }

    public int Length => count;

    public void Write(byte[] data, int offset, int length)
    {
        if (length > buffer.Length - count)
            throw new InvalidOperationException("缓冲区已满，无法写入更多数据");

        for (int i = 0; i < length; i++)
        {
            buffer[tail] = data[offset + i];
            tail = (tail + 1) % buffer.Length;
        }

        count += length;
    }

    public void Read(int length, out byte[] data)
    {
        if (length > count)
            throw new InvalidOperationException("没有足够的数据可供读取");

        data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = buffer[head];
            head = (head + 1) % buffer.Length;
        }

        count -= length;
    }

    public void Peek(int offset, int length, out byte[] data)
    {
        if (offset + length > count)
            throw new InvalidOperationException("没有足够的数据可供查看");

        data = new byte[length];
        int tempHead = head;
        for (int i = 0; i < offset; i++)
            tempHead = (tempHead + 1) % buffer.Length;

        for (int i = 0; i < length; i++)
        {
            data[i] = buffer[tempHead];
            tempHead = (tempHead + 1) % buffer.Length;
        }
    }

    public void Discard(int length)
    {
        if (length > count)
            throw new InvalidOperationException("没有足够的数据可供丢弃");

        head = (head + length) % buffer.Length;
        count -= length;
    }
}
