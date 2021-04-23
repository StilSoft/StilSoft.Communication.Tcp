using System;
using System.Threading.Tasks;
using StilSoft.Communication.Tcp.EventsArgs;

namespace StilSoft.Communication.Tcp
{
    public interface ITcpClient
    {
        event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        event EventHandler<DataReceivedEventArgs> DataReceived;
        event EventHandler<ErrorEventArgs> InternalError;

        ConnectionState State { get; }
        string Hostname { get; set; }
        int Port { get; set; }
        int ConnectTimeOut { get; set; }
        int SendTimeOut { get; set; }
        int ReceiveTimeOut { get; set; }
        int SendBufferSize { get; set; }
        int ReceiveBufferSize { get; set; }
        bool AutoConnect { get; set; }
        bool AutoReconnect { get; set; }
        int AutoReconnectInterval { get; set; }
        int ReconnectAttempts { get; set; }
        bool KeepAliveEnable { get; set; }
        int KeepAliveTime { get; set; }
        int KeepAliveInterval { get; set; }
        void Dispose();
        Task ConnectAsync();
        Task ConnectAsync(string hostname, int port);
        void Connect();
        void Connect(string hostname, int port);
        Task DisconnectAsync();
        void Disconnect();
        bool IsConnected();
        void Send(byte[] data);
        void SendReceive(byte[] data, Func<byte[], bool> dataReceiver, bool clearReceiveBuffer = false);
    }
}