// ConnectTogether.App/Services/PortInspector.cs

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ConnectTogether.App.Services
{
    /// <summary>
    /// Проверки UDP-порта приложения: занят ли он, какой программой, и поиск свободного.
    /// </summary>
    public static class PortInspector
    {
        public static bool IsUdpPortInUse(int port) =>
            IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(l => l.Port == port);

        /// <summary>Описание владельца порта для «Подробнее»: «netgame.exe (PID 4120)» или null.</summary>
        public static string? DescribeUdpOwner(int port)
        {
            int? pid = FindUdpOwnerPid(port);
            if (pid == null) return null;

            try
            {
                using var process = Process.GetProcessById(pid.Value);
                return $"{process.ProcessName}.exe (PID {pid})";
            }
            catch (ArgumentException)
            {
                return $"PID {pid}"; // Процесс уже завершился
            }
        }

        /// <summary>Ищет свободный UDP-порт начиная с preferred + 1.</summary>
        public static int FindFreeUdpPort(int preferred)
        {
            for (int port = preferred + 1; port < preferred + 200 && port <= 65535; port++)
            {
                if (CanBind(port)) return port;
            }

            using var any = new UdpClient(0);
            return ((IPEndPoint)any.Client.LocalEndPoint!).Port;
        }

        private static bool CanBind(int port)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private const int AfInet = 2;
        private const int UdpTableOwnerPid = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct UdpRowOwnerPid
        {
            public uint LocalAddr;
            public uint LocalPort;
            public uint OwningPid;
        }

        private static int? FindUdpOwnerPid(int port)
        {
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableOwnerPid, 0);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableOwnerPid, 0) != 0) return null;

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<UdpRowOwnerPid>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<UdpRowOwnerPid>(buffer + 4 + i * rowSize);
                    // Порт хранится в сетевом порядке байт в младших 16 битах
                    int rowPort = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    if (rowPort == port) return (int)row.OwningPid;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
