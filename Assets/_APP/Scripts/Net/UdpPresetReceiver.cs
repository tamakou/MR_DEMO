// Assets/_APP/Scripts/Net/UdpPresetReceiver.cs

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public sealed class UdpPresetReceiver : MonoBehaviour
{
  [SerializeField] int listenPort = 50000;
  [SerializeField] PresetManager presetManager;

  UdpClient udpClient;
  Thread receiveThread;
  volatile bool running = true;

  readonly object _lock = new();
  string _latestPresetName;

  void Awake()
  {
    if (presetManager == null)
      presetManager = FindFirstObjectByType<PresetManager>();

    try
    {
      // 明示的に IPv4 にしておく
      udpClient = new UdpClient(listenPort);
      receiveThread = new Thread(ReceiveLoop);
      receiveThread.IsBackground = true;
      receiveThread.Start();
      Debug.Log($"[UdpPresetReceiver] UDP listen start. Port={listenPort}");
    }
    catch (Exception e)
    {
      Debug.LogError("[UdpPresetReceiver] UDP 初期化失敗: " + e);
    }
  }

  void ReceiveLoop()
  {
    Debug.Log("[UdpPresetReceiver] ReceiveLoop started");

    var remoteEP = new IPEndPoint(IPAddress.Any, 0);

    try
    {
      while (running)
      {
        byte[] data = udpClient.Receive(ref remoteEP);   // ここでブロック待ち
        string msg = Encoding.UTF8.GetString(data).Trim();
        Debug.Log($"[UdpPresetReceiver] 受信: '{msg}' from {remoteEP.Address}");

        lock (_lock)
        {
          _latestPresetName = msg;
        }
      }
    }
    catch (SocketException e)
    {
      if (running)
        Debug.LogError("[UdpPresetReceiver] SocketException: " + e);
    }
    catch (ObjectDisposedException)
    {
      // Close 時など
    }

    Debug.Log("[UdpPresetReceiver] ReceiveLoop end");
  }

  void Update()
  {
    string presetName = null;
    lock (_lock)
    {
      presetName = _latestPresetName;
      _latestPresetName = null;
    }

    if (string.IsNullOrEmpty(presetName)) return;
    if (presetManager == null)
    {
      Debug.LogError("[UdpPresetReceiver] presetManager が null");
      return;
    }

    Debug.Log("[UdpPresetReceiver] Apply preset from UDP: " + presetName);
    presetManager.ApplyPresetResource(presetName);
  }

  void OnDestroy()
  {
    running = false;
    try { udpClient?.Close(); } catch { }
    try { receiveThread?.Join(100); } catch { }
  }
}
