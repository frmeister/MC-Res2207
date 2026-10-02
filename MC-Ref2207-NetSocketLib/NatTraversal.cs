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

            // RunContinuationsAsynchronously: иначе код после await выполнялся бы прямо в цикле приёма UdpPeer и блокировал его
            var tcs = new TaskCompletionSource<IPEndPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
            IPEndPoint? acceptedClient = null;
            byte[] ack = Encoding.ASCII.GetBytes("ACK");

            void OnDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
            {
                // e.Data - массив байт
                // e.RemoteEndPoint - IPEndPoint отправителя
                if (e.Data.Length < 5 || Encoding.ASCII.GetString(e.Data, 0, 5) != "HELLO") return;

                if (acceptedClient == null)
                {
                    // Первый HELLO — запоминаем удалённую точку и сигнализируем об успехе
                    acceptedClient = e.RemoteEndPoint;
                    peer.SetRemoteEndPoint(e.RemoteEndPoint);
                    tcs.TrySetResult(e.RemoteEndPoint);
                }
                else if (!e.RemoteEndPoint.Equals(acceptedClient))
                {
                    return; // HELLO от другого клиента — уже работаем с первым
                }

                // Отвечаем ACK на каждый HELLO принятого клиента: первый ACK мог потеряться,
                // и тогда клиент продолжает слать HELLO, пока не получит ответ.
                // Используем SendToAsync, так как соединение еще не установлено
                _ = peer.SendToAsync(ack, e.RemoteEndPoint); // Не ждем завершения отправки
            }

            // Подписываемся на событие еще до отправления HELLO
            peer.DataReceived += OnDataReceived;

            bool accepted = false;
            try
            {
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
                            accepted = true;
                            return result;
                        }
                        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            // Таймаут
                            throw new TimeoutException("Timed out waiting for client.");
                        }
                    }
                }
            }
            finally
            {
                if (accepted)
                {
                    // Ещё столько же, сколько клиент может ждать ACK, отвечаем на его повторные HELLO
                    _ = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)).ContinueWith(_ => peer.DataReceived -= OnDataReceived);
                }
                else
                {
                    peer.DataReceived -= OnDataReceived; // Таймаут или внешняя отмена
                }
            }
        }


        public static async Task<bool> ConnectToHostAsync(UdpPeer peer, IPEndPoint hostEndPoint, CancellationToken cancellationToken = default)
        {
            if (peer == null || hostEndPoint == null) throw new ArgumentNullException(peer == null ? nameof(peer) : nameof(hostEndPoint));

            byte[] helloData = Encoding.ASCII.GetBytes("HELLO");

            // Здесь нужно дождаться ACK. Используем TaskCompletionSource и подписку на событие.
            // RunContinuationsAsynchronously: иначе код после await выполнялся бы прямо в цикле приёма UdpPeer и блокировал его
            var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDataReceived(object? sender, UdpPeer.UdpDataReceivedEventArgs e)
            {
                if (e.RemoteEndPoint.Equals(hostEndPoint) &&
                e.Data.Length >= 3 && Encoding.ASCII.GetString(e.Data, 0, 3) == "ACK")
                {
                    ackTcs.TrySetResult(true);
                }
            }

            peer.DataReceived += OnDataReceived;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                // Шлём HELLO, пока не придёт ACK или не истечёт таймаут
                var senderTask = Task.Run(async () =>
                {
                    while (!ackTcs.Task.IsCompleted && !timeoutCts.IsCancellationRequested)
                    {
                        try
                        {
                            await peer.SendToAsync(helloData, hostEndPoint);
                        }
                        catch (Exception ex)
                        {
                            // Разовая ошибка сети не должна прекращать попытки до таймаута
                            Debug.WriteLine($"[Core.Network.NatTraversal] Failed to send HELLO: {ex.Message}");
                        }
                        try { await Task.Delay(HelloIntervalMs, timeoutCts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                });

                bool acked;
                using (timeoutCts.Token.Register(() => ackTcs.TrySetCanceled(timeoutCts.Token)))
                {
                    try
                    {
                        await ackTcs.Task; // завершится сразу, как только придёт ACK
                        acked = true;
                    }
                    catch (OperationCanceledException)
                    {
                        acked = false;
                    }
                }

                // Останавливаем отправку HELLO до освобождения timeoutCts
                timeoutCts.Cancel();
                await senderTask;

                if (acked) peer.SetRemoteEndPoint(hostEndPoint);
                return acked;
            }
            finally
            {
                peer.DataReceived -= OnDataReceived;
            }
        }

        // Этот метод не относится к NAT traversal, лучше вынести в отдельный класс
        // public async Task GetClientAddress() { ... }
    }
}