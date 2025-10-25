using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PatientMonitoring.Services
{
    public static class PythonBackendControl
    {
        public static async Task RequestShutdownAsync(int timeoutMs = 1000)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", 12346, cts.Token);

                var stream = client.GetStream();
                var msg = Encoding.UTF8.GetBytes("SHUTDOWN\n");
                await stream.WriteAsync(msg, cts.Token);

                // Optional: wait for ACK
                var buffer = new byte[16];
                _ = await stream.ReadAsync(buffer, cts.Token);
            }
            catch
            {
                // Ignore errors if backend is already down or not reachable
            }
        }
    }
}