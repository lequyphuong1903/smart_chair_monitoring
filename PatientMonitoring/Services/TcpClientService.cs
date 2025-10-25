using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace PatientMonitoring.Services
{
    public class TcpSensorClient
    {
        private readonly string _host;
        private readonly int _port;

        public event Action<short, short, uint, uint, ushort, ushort>? OnPayload;
        public event Action<string>? OnStatus;
        public event Action<Exception>? OnError;

        public TcpSensorClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                NetworkStream? stream = null;
                try
                {
                    OnStatus?.Invoke($"Kết nối tới {_host}:{_port} ...");
                    client = new TcpClient();
                    var connectTask = client.ConnectAsync(_host, _port);
#if NET6_0_OR_GREATER
                    using (token.Register(() => { try { client.Close(); } catch { } }))
#endif
                    {
                        await connectTask;
                    }
                    if (!client.Connected)
                        throw new Exception("Cannot connect to server.");

                    OnStatus?.Invoke("Streaming");
                    stream = client.GetStream();
                    var buffer = new byte[16];

                    while (!token.IsCancellationRequested)
                    {
                        int read = 0;
                        while (read < 16)
                        {
                            int r = await stream.ReadAsync(buffer.AsMemory(read, 16 - read), token);
                            if (r == 0)
                                throw new Exception("Server is closed.");
                            read += r;
                        }

                        short v2 = BitConverter.ToInt16(buffer, 0);
                        short v1 = BitConverter.ToInt16(buffer, 2);
                        uint v3 = BitConverter.ToUInt32(buffer, 4);
                        uint v4 = BitConverter.ToUInt32(buffer, 8);
                        ushort v5 = BitConverter.ToUInt16(buffer, 12);
                        ushort v6 = BitConverter.ToUInt16(buffer, 14);

                        OnPayload?.Invoke(v1, v2, v3, v4, v5, v6);
                    }
                }
                catch (OperationCanceledException)
                {
                    OnStatus?.Invoke("Đã hủy.");
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex);
                    OnStatus?.Invoke("Lỗi: " + ex.Message + " (thử lại sau 2s)");
                    try { await Task.Delay(2000, token); } catch { }
                }
                finally
                {
                    try { stream?.Dispose(); } catch { }
#if NETSTANDARD2_0
                    try { client?.Close(); } catch { }
#else
                    try { client?.Dispose(); } catch { }
#endif
                }
            }
        }
    }
}