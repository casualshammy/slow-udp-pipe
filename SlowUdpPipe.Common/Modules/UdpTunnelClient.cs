﻿using Ax.Fw.Crypto;
using Ax.Fw.Extensions;
using Ax.Fw.SharedTypes.Interfaces;
using JustLogger.Interfaces;
using SlowUdpPipe.Common.Data;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;

namespace SlowUdpPipe.Common.Modules;

public class UdpTunnelClient
{
  record ClientInfo(Socket Socket, ILifetime Lifetime)
  {
    public long Timestamp { get; set; }
  };

  private readonly UdpTunnelClientOptions p_options;
  private readonly IReadOnlyLifetime p_lifetime;
  private readonly ILogger p_logger;
  private readonly ConcurrentDictionary<EndPoint, ClientInfo> p_clients = new();
  private readonly ReplaySubject<UdpTunnelStat> p_stats;
  private ulong p_byteRecvCount;
  private ulong p_byteSentCount;

  public UdpTunnelClient(
    UdpTunnelClientOptions _options,
    IReadOnlyLifetime _lifetime,
    ILogger _logger)
  {
    p_options = _options;
    p_lifetime = _lifetime;
    p_logger = _logger;
    p_stats = _lifetime.ToDisposeOnEnded(new ReplaySubject<UdpTunnelStat>(1));

    var localServiceSocket = _lifetime.ToDisposeOnEnded(new Socket(p_options.Local.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp));
    try
    {
      localServiceSocket.Bind(p_options.Local);
    }
    catch (SocketException sex) when (sex.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
      p_logger.Error($"Can't bind to address {p_options.Local}: already in use");
      return;
    }
    catch (Exception ex)
    {
      p_logger.Error($"Can't bind to address {p_options.Local}: {ex.Message}");
      return;
    }

    var localServiceSocketRoutineThread = new Thread(() => CreateLocalServiceRoutine(localServiceSocket, _lifetime)) { Priority = ThreadPriority.Highest };
    localServiceSocketRoutineThread.Start();

    Observable
      .Interval(TimeSpan.FromSeconds(1))
      .StartWithDefault()
      .Scan((Sent: 0UL, Recv: 0UL, Timestamp: 0L), (_acc, _) =>
      {
        var tickCount = Environment.TickCount64;
        var tx = Interlocked.Read(ref p_byteSentCount);
        var rx = Interlocked.Read(ref p_byteRecvCount);

        var delta = TimeSpan.FromMilliseconds(tickCount - _acc.Timestamp).TotalSeconds;
        if (delta == 0d)
          return _acc;

        var txDelta = (ulong)((tx - _acc.Sent) / delta);
        var rxDelta = (ulong)((rx - _acc.Recv) / delta);
        p_stats.OnNext(new UdpTunnelStat(txDelta, rxDelta));

        return (tx, rx, tickCount);
      })
      .Subscribe(_lifetime);

    Observable
      .Interval(TimeSpan.FromSeconds(60))
      .Subscribe(__ =>
      {
        var now = Environment.TickCount64;
        foreach (var (endPoint, clientInfo) in p_clients)
          if (now - clientInfo.Timestamp > 2 * 60 * 1000)
            if (p_clients.TryRemove(endPoint, out var removedClientInfo))
            {
              removedClientInfo.Lifetime.End();
              p_logger.Info($"[{endPoint}] Client is disconnected due to inactivity; total clients: {p_clients.Count}");
            }
      }, _lifetime);
  }

  public IObservable<UdpTunnelStat> Stats => p_stats;

  private void CreateLocalServiceRoutine(
    Socket _localServiceSocket,
    IReadOnlyLifetime _lifetime)
  {
    Span<byte> buffer = new byte[128 * 1024];
    EndPoint localServiceEndpoint = new IPEndPoint(IPAddress.Any, short.MaxValue);
    var remoteEndPoint = p_options.Remote;
    var cryptor = GetCrypto(_lifetime);

    p_logger.Info($"Local service socket routine is started");

    while (!_lifetime.IsCancellationRequested)
    {
      try
      {
        var receivedBytes = _localServiceSocket.ReceiveFrom(buffer, SocketFlags.None, ref localServiceEndpoint);
        if (receivedBytes > 0)
        {
          var dataToSend = cryptor.Encrypt(buffer.Slice(0, receivedBytes));

          if (!p_clients.TryGetValue(localServiceEndpoint, out var client))
          {
            p_clients.TryAdd(localServiceEndpoint, client = AllocateNewClient(_localServiceSocket, localServiceEndpoint));
            p_logger.Info($"[{localServiceEndpoint}] Client connected; total clients: {p_clients.Count}");
          }

          client.Timestamp = Environment.TickCount64;
          client.Socket.SendTo(dataToSend, SocketFlags.None, remoteEndPoint);
          Interlocked.Add(ref p_byteSentCount, (ulong)dataToSend.Length);
        }
      }
      catch (SocketException sex0) when (sex0.ErrorCode == 10004) // Interrupted function call
      {
        // ignore (caused by client disconnection)
      }
      catch (SocketException sex1) when (sex1.ErrorCode == 10054) // Connection reset by peer.
      {
        // ignore (caused by client disconnection)
      }
      catch (SocketException sexi)
      {
        p_logger.Error($"Local interface SocketException error; code: '{sexi.ErrorCode}'", sexi);
      }
      catch (Exception ex)
      {
        p_logger.Error("Local interface error", ex);
      }
    }

    p_logger.Info($"Local service socket routine is ended");
  }

  private void CreateRemoteServerRoutine(
    Socket _remoteServerSocket,
    Socket _localServiceSocket,
    EndPoint _localServiceEndpoint,
    IReadOnlyLifetime _lifetime)
  {
    Span<byte> buffer = new byte[128 * 1024];
    EndPoint _ = new IPEndPoint(IPAddress.Any, short.MaxValue);
    var cryptor = GetCrypto(_lifetime);

    p_logger.Info($"[{_localServiceEndpoint}] Remote server socket routine is started");

    while (!_lifetime.IsCancellationRequested)
    {
      try
      {
        if (!_remoteServerSocket.IsBound)
        {
          Thread.Sleep(100);
          continue;
        }

        var receivedBytes = _remoteServerSocket.ReceiveFrom(buffer, SocketFlags.None, ref _);
        if (receivedBytes > 0)
        {
          Interlocked.Add(ref p_byteRecvCount, (ulong)receivedBytes);
          var dataToSend = cryptor.Decrypt(buffer.Slice(0, receivedBytes));
          _localServiceSocket.SendTo(dataToSend, SocketFlags.None, _localServiceEndpoint);
        }
      }
      catch (SocketException sex0) when (sex0.ErrorCode == 10004) // Interrupted function call
      {
        // ignore (caused be client cleanup)
      }
      catch (SocketException sex1) when (sex1.ErrorCode == 10054) // Connection reset by peer
      {
        p_logger.Error($"Remote host is not responding");
      }
      catch (Exception ex)
      {
        p_logger.Error("Remote interface error", ex);
      }
    }

    p_logger.Info($"[{_localServiceEndpoint}] Remote server socket routine is ended");
  }

  private ClientInfo AllocateNewClient(
    Socket _localServiceSocket,
    EndPoint _localServiceEndpoint)
  {
    var lifetime = p_lifetime.GetChildLifetime();
    if (lifetime != null)
    {
      var remoteServerSocket = lifetime.ToDisposeOnEnding(new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp));
      var thread = new Thread(() => CreateRemoteServerRoutine(
                   remoteServerSocket,
                   _localServiceSocket,
                   _localServiceEndpoint,
                   lifetime))
      { Priority = ThreadPriority.Highest };
      thread.Start();

      return new(remoteServerSocket, lifetime) { Timestamp = Environment.TickCount64 };
    }

    throw new OperationCanceledException();
  }

  private ICryptoAlgorithm GetCrypto(IReadOnlyLifetime _lifetime)
  {
    return p_options.Cipher switch
    {
      EncryptionAlgorithm.Aes128 => new AesCbc(_lifetime, p_options.Key, 128),
      EncryptionAlgorithm.Aes256 => new AesCbc(_lifetime, p_options.Key, 256),
      EncryptionAlgorithm.AesGcm128 => new AesWithGcm(_lifetime, p_options.Key, 128),
      EncryptionAlgorithm.AesGcm256 => new AesWithGcm(_lifetime, p_options.Key, 256),
      EncryptionAlgorithm.AesGcmObfs128 => new AesWithGcmObfs(_lifetime, p_options.Key, Consts.MAX_UDP_PACKET_PAYLOAD_SIZE, 128),
      EncryptionAlgorithm.AesGcmObfs256 => new AesWithGcmObfs(_lifetime, p_options.Key, Consts.MAX_UDP_PACKET_PAYLOAD_SIZE, 256),
      EncryptionAlgorithm.ChaCha20Poly1305 => new ChaCha20WithPoly1305(_lifetime, p_options.Key),
      EncryptionAlgorithm.Xor => new Xor(Encoding.UTF8.GetBytes(p_options.Key)),
      _ => throw new InvalidOperationException($"Crypto algorithm is not specified!"),
    };
  }

}
