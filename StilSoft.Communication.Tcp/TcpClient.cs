using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using StilSoft.Communication.Tcp.EventsArgs;
using Timer = System.Timers.Timer;

namespace StilSoft.Communication.Tcp
{
    public class TcpClient : IDisposable, ITcpClient
    {
        private readonly object connectionLock = new object();
        private readonly Timer autoReconnectTimer;
        private readonly ManualResetEvent sendReceiveDataEvent;
        private bool disposed;

        private System.Net.Sockets.TcpClient client;
        private Task receiveTask;
        private int reconnectAttemptsCounter;
        private CancellationTokenSource receiveCancellationTokenSource;
        private bool keepAliveEnable;
        private int keepAliveTime = 1000;
        private int keepAliveInterval = 100;
        private bool externalDisconnectCall;

        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<ErrorEventArgs> InternalError;

        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
        public string Hostname { get; set; }
        public int Port { get; set; }
        public int ConnectTimeOut { get; set; } = 2000;
        public int SendTimeOut { get; set; } = 2000;
        public int ReceiveTimeOut { get; set; } = 2000;
        public int SendBufferSize { get; set; } = 16 * 1024;
        public int ReceiveBufferSize { get; set; } = 16 * 1024;
        public bool AutoConnect { get; set; }
        public bool AutoReconnect { get; set; }

        public int AutoReconnectInterval
        {
            get => (int)this.autoReconnectTimer.Interval;
            set => this.autoReconnectTimer.Interval = value;
        }

        public int ReconnectAttempts { get; set; } = 10;

        public bool KeepAliveEnable
        {
            get => this.keepAliveEnable;
            set
            {
                this.keepAliveEnable = value;

                KeepAlive(this.KeepAliveEnable, this.KeepAliveTime, this.KeepAliveInterval);
            }
        }

        public int KeepAliveTime
        {
            get => this.keepAliveTime;
            set
            {
                this.keepAliveTime = value;

                KeepAlive(this.KeepAliveEnable, this.KeepAliveTime, this.KeepAliveInterval);
            }
        }

        public int KeepAliveInterval
        {
            get => this.keepAliveInterval;
            set
            {
                this.keepAliveInterval = value;

                KeepAlive(this.KeepAliveEnable, this.KeepAliveTime, this.KeepAliveInterval);
            }
        }


        public TcpClient()
        {
            this.autoReconnectTimer = new Timer { Interval = 1000 };
            this.autoReconnectTimer.Elapsed += AutoReconnectTimer_Elapsed;
            this.autoReconnectTimer.AutoReset = false;

            this.sendReceiveDataEvent = new ManualResetEvent(false);
        }

        public TcpClient(string hostname, int port)
            : this()
        {
            this.Hostname = hostname;
            this.Port = port;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                DisconnectInternal();
            }

            this.disposed = true;
        }

        ~TcpClient()
        {
            Dispose(false);
        }


        public Task ConnectAsync()
        {
            return ConnectAsync(this.Hostname, this.Port);
        }

        public Task ConnectAsync(string hostname, int port)
        {
            return Task.Run(() => ConnectInternal(hostname, port));
        }

        public void Connect()
        {
            ConnectInternal(this.Hostname, this.Port);
        }

        public void Connect(string hostname, int port)
        {
            ConnectInternal(hostname, port);
        }

        private void ConnectInternal(string hostname, int port, bool reconnect = false)
        {
            lock (this.connectionLock)
            {
                this.Hostname = hostname;
                this.Port = port;

                DisconnectInternal();

                this.client = new System.Net.Sockets.TcpClient
                {
                    SendTimeout = this.SendTimeOut,
                    SendBufferSize = this.SendBufferSize,
                    ReceiveBufferSize = this.ReceiveBufferSize,
                    NoDelay = true
                };

                KeepAlive(this.KeepAliveEnable, this.KeepAliveTime, this.KeepAliveInterval);

                try
                {
                    OnConnectionStateChanged(reconnect ? ConnectionState.Reconnecting : ConnectionState.Connecting);

                    bool isConnectComplete = this.client.ConnectAsync(hostname, port).Wait(this.ConnectTimeOut);

                    if (!isConnectComplete)
                    {
                        throw new TimeoutException("Connect timeout.");
                    }
                }
                catch (Exception ex)
                {
                    DisconnectInternal();

                    if (ex is AggregateException && ex.InnerException is SocketException)
                    {
                        throw ex.InnerException;
                    }

                    throw;
                }

                if (!IsConnected())
                {
                    DisconnectInternal();

                    throw new InvalidOperationException("Unable to connect.");
                }

                this.externalDisconnectCall = false;
                this.reconnectAttemptsCounter = this.ReconnectAttempts;

                StartReceiveTask();

                OnConnectionStateChanged(ConnectionState.Connected);
            }
        }

        public Task DisconnectAsync()
        {
            return Task.Run(() => Disconnect());
        }

        public void Disconnect()
        {
            this.externalDisconnectCall = true;

            DisconnectInternal();
        }

        private void DisconnectInternal()
        {
            lock (this.connectionLock)
            {
                this.autoReconnectTimer.Enabled = false;

                StopReceiveTask();

                this.sendReceiveDataEvent.Set();
                this.sendReceiveDataEvent.Reset();

                this.client?.Close();
                this.client = null;

                try
                {
                    this.receiveTask?.Wait(500);
                }
                catch
                {
                    // ignored
                }

                if (this.State != ConnectionState.Disconnected)
                {
                    OnConnectionStateChanged(ConnectionState.Disconnected);
                }
            }
        }

        public bool IsConnected()
        {
            bool checkIsConnected = this.client != null &&
                                    this.client.Connected &&
                                    !(this.client.Client.Poll(0, SelectMode.SelectRead) && this.client.Available == 0) &&
                                    this.client.Client.Poll(0, SelectMode.SelectWrite) &&
                                    !this.client.Client.Poll(0, SelectMode.SelectError);

            return checkIsConnected;
        }

        public void Send(byte[] data)
        {
            SendReceive(data, null);
        }

        public void SendReceive(byte[] data, Func<byte[], bool> dataReceiver, bool clearReceiveBuffer = false)
        {
            lock (this.connectionLock)
            {
                if (!IsConnected())
                {
                    if (!this.AutoConnect)
                    {
                        throw new InvalidOperationException(
                            "A request to send data was disallowed because the socket is not connected.");
                    }

                    Connect();
                }

                // Clear received before sending new data
                if (clearReceiveBuffer)
                {
                    while (this.client.Available > 0)
                    {
                        byte[] dummy = new byte[this.client.Available];
                        this.client.Client.Receive(dummy);
                    }
                }

                // Send data
                this.client.Client.Send(data);

                if (dataReceiver == null)
                {
                    return;
                }

                // Receive data if data receiver function is set
                EventHandler<DataReceivedEventArgs> receivedDataHandler = (sender, e) =>
                {
                    // Return if event is set
                    if (this.sendReceiveDataEvent.WaitOne(0))
                    {
                        return;
                    }

                    // Set event If data receiver return true
                    if (dataReceiver(e.Data))
                    {
                        this.sendReceiveDataEvent.Set();
                    }
                };

                this.DataReceived += receivedDataHandler;

                // Wait data receiver to complete
                bool isReceiverComplete = this.sendReceiveDataEvent.WaitOne(this.ReceiveTimeOut);

                this.DataReceived -= receivedDataHandler;

                this.sendReceiveDataEvent.Reset();

                // If data receiver function not return true in specified "ReceiveTimeOut" time then throw "TimeoutException"
                if (!isReceiverComplete)
                {
                    throw new TimeoutException("Timeout receiving data.");
                }
            }
        }

        private void StartReceiveTask()
        {
            this.receiveCancellationTokenSource = new CancellationTokenSource();

            this.receiveTask = Task.Factory.StartNew(() => ReceiveThread(this.receiveCancellationTokenSource.Token),
                this.receiveCancellationTokenSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void StopReceiveTask()
        {
            this.receiveCancellationTokenSource?.Cancel();
        }

        private void ReceiveThread(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    byte[] receiveBuffer = new byte[this.ReceiveBufferSize];
                    int totalBytesReceived = this.client.Client.Receive(receiveBuffer);

                    if (totalBytesReceived == 0)
                    {
                        throw new InvalidOperationException("Connection lost.");
                    }

                    byte[] buffer = new byte[totalBytesReceived];
                    Array.Copy(receiveBuffer, buffer, totalBytesReceived);

                    OnDataReceived(buffer);
                }
                catch (Exception ex)
                {
                    var socketException = ex as SocketException;

                    if (socketException?.SocketErrorCode == SocketError.Interrupted)
                    {
                        break;
                    }

                    DisconnectInternal();

                    if (this.AutoReconnect && !this.externalDisconnectCall)
                    {
                        this.autoReconnectTimer.Enabled = true;
                    }

                    OnInternalError(ex);

                    break;
                }
            }
        }

        private void AutoReconnectTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!this.AutoReconnect || IsConnected() || this.reconnectAttemptsCounter == 0 || this.externalDisconnectCall)
            {
                return;
            }

            this.reconnectAttemptsCounter--;

            try
            {
                ConnectInternal(this.Hostname, this.Port, true);
            }
            catch
            {
                this.autoReconnectTimer.Enabled = this.AutoReconnect;
            }
        }

        private void OnConnectionStateChanged(ConnectionState state)
        {
            this.State = state;

            this.ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state));
        }

        private void OnDataReceived(byte[] data)
        {
            this.DataReceived?.Invoke(this, new DataReceivedEventArgs(data));
        }

        private void OnInternalError(Exception ex)
        {
            try
            {
                this.InternalError?.Invoke(this, new ErrorEventArgs(ex));
            }
            catch
            {
                // ignored
            }
        }

        private void KeepAlive(bool enable, int keepAliveTime, int keepAliveInterval)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            if (keepAliveTime <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keepAliveTime));
            }

            if (keepAliveInterval <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keepAliveInterval));
            }

            int size = Marshal.SizeOf(new uint());
            byte[] values = new byte[size * 3];

            BitConverter.GetBytes((uint)(enable ? 1 : 0)).CopyTo(values, 0);
            BitConverter.GetBytes((uint)keepAliveTime).CopyTo(values, size);
            BitConverter.GetBytes((uint)keepAliveInterval).CopyTo(values, size * 2);

            this.client?.Client?.IOControl(IOControlCode.KeepAliveValues, values, null);
        }
    }
}