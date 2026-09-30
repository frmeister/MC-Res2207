using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MCTunnel.Core.Network
{
    public class NatTraversal
    {
        // Таймеры и кулдауны
        private const int HelloIntervalMs = 100;
        private const int TimeoutSeconds = 10; // Увеличим таймаут для тестирования NAT

        // Метод для хоста: ожидает входящее соединение от клиента
        public static async Task<IPEndPoint?> WaitForClientAsync(UdpPeer peer, CancellationToken cancellationToken = default)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));

            var tcs = new TaskCompletionSource<IPEndPoint?>();

            void OnDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
            {
                // e.Data - массив байт
                // e.RemoteEndPoint - IPEndPoint отправителя
                if (e.Data.Length >= 5 && Encoding.ASCII.GetString(e.Data, 0, 5) == "HELLO")
                {
                    // Отписываемся, чтобы не обрабатывать лишние HELLO
                    peer.DataReceived -= OnDataReceived;

                    // Запоминаем удалённую точку
                    peer.SetRemoteEndPoint(e.RemoteEndPoint);

                    // Отправляем ACK
                    byte[] ack = Encoding.ASCII.GetBytes("ACK");
                    // Используем SendToAsync, так как соединение еще не установлено
                    _ = peer.SendToAsync(ack, e.RemoteEndPoint); // Не ждем завершения отправки

                    // Сигнализируем об успехе
                    tcs.TrySetResult(e.RemoteEndPoint);
                }
            }

            // Подписываемся на событие еще до отправления HELLO
            peer.DataReceived += OnDataReceived;


            // Ожидаем результат или таймаут/отмену
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                // Регистрируем отмену таска при срабатывании таймаута или внешней отмены
                using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
                {
                    try
                    {
                        var result = await tcs.Task;
                        return result;
                    }
                    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // Таймаут
                        peer.DataReceived -= OnDataReceived; // Убедимся, что отписались
                        throw new TimeoutException("Timed out waiting for client.");
                    }
                }
            }
        }


        public static async Task<bool> ConnectToHostAsync(UdpPeer peer, IPEndPoint hostEndPoint, CancellationToken cancellationToken = default)
        {
            if (peer == null || hostEndPoint == null) throw new ArgumentNullException(peer == null ? nameof(peer) : nameof(hostEndPoint));

            byte[] helloData = Encoding.ASCII.GetBytes("HELLO");

            // Здесь нужно дождаться ACK. Используем TaskCompletionSource и подписку на событие.
            var ackTcs = new TaskCompletionSource<bool>();

            // var stopwatch = Stopwatch.StartNew();

            void OnDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
            {
                if (e.RemoteEndPoint.Equals(hostEndPoint) &&
                e.Data.Length >= 3 && Encoding.ASCII.GetString(e.Data, 0, 3) == "ACK")
                {
                    peer.DataReceived -= OnDataReceived;
                    ackTcs.TrySetResult(true);
                }
            }

            peer.DataReceived += OnDataReceived;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            // Запускаем таймаут
            var senderTask = Task.Run(async () =>
            {
                while (!ackTcs.Task.IsCompleted && !timeoutCts.IsCancellationRequested)
                {
                    await peer.SendToAsync(helloData, hostEndPoint);
                    try { await Task.Delay(HelloIntervalMs, timeoutCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });

            using (timeoutCts.Token.Register(() => ackTcs.TrySetCanceled(timeoutCts.Token)))
            {
                try
                {
                    await ackTcs.Task; // завершится сразу, как только придёт ACK
                    peer.SetRemoteEndPoint(hostEndPoint);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    peer.DataReceived -= OnDataReceived;
                    return false;
                }
            }
        }

        // Этот метод не относится к NAT traversal, лучше вынести в отдельный класс
        // public async Task GetClientAddress() { ... }
    }
}