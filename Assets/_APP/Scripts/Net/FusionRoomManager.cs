using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class FusionRoomManager : MonoBehaviour, INetworkRunnerCallbacks
{
  [Header("Fusion Prefabs")]
  [SerializeField] private NetworkRunner runnerPrefab;
  [SerializeField] private NetworkObject outmeshPrefab;

  [Header("Room Buttons")]
  [SerializeField] private Button roomTButton;
  [SerializeField] private Button roomJButton;
  [SerializeField] private Button roomDevButton;

  private NetworkRunner _runner;
  private NetworkObject _outmeshInstance;

  private void Awake()
  {
    if (roomTButton != null)
      roomTButton.onClick.AddListener(() => StartRoom("RoomT"));
    if (roomJButton != null)
      roomJButton.onClick.AddListener(() => StartRoom("RoomJ"));
    if (roomDevButton != null)
      roomDevButton.onClick.AddListener(() => StartRoom("RoomDev"));
  }

  /// <summary>
  /// 指定したセッション名(RoomT / RoomJ / RoomDev)で
  /// AutoHostOrClient で Host / Client として入室
  /// </summary>
  private async void StartRoom(string sessionName)
  {
    if (_runner != null)
    {
      Debug.LogWarning($"Already in a session: {_runner.SessionInfo.Name}");
      return;
    }

    // NetworkRunner プレハブからインスタンス生成
    _runner = Instantiate(runnerPrefab);
    _runner.name = "NetworkRunner";
    _runner.ProvideInput = false; // 今はプレイヤー入力を Fusion 経由で送らない

    // このクラスのコールバックを登録
    _runner.AddCallbacks(this);

    var scene = SceneManager.GetActiveScene();
    var sceneRef = SceneRef.FromIndex(scene.buildIndex);
    var sceneManager = _runner.GetComponent<NetworkSceneManagerDefault>();

    var startArgs = new StartGameArgs
    {
      // ★ Fusion2 正式値：AutoHostOrClient
      GameMode = GameMode.AutoHostOrClient,
      SessionName = sessionName,
      Scene = sceneRef,
      SceneManager = sceneManager
    };

    var result = await _runner.StartGame(startArgs);
    if (!result.Ok)
    {
      Debug.LogError($"StartGame failed: {result.ShutdownReason}");
    }
    else
    {
      Debug.Log($"Joined session: {sessionName}, IsServer={_runner.IsServer}");
    }
  }

  // =========================
  // INetworkRunnerCallbacks
  // =========================

  public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
  {
    Debug.Log($"Player joined: {player}");

    // ホスト側で一度だけ outmesh を Spawn
    if (runner.IsServer && _outmeshInstance == null && outmeshPrefab != null)
    {
      // もともとシーンで置いていた座標をそのまま使用
      Vector3 pos = new Vector3(0f, 0f, 1.3f);
      Quaternion rot = Quaternion.Euler(-90f, 0f, 0f);

      _outmeshInstance = runner.Spawn(outmeshPrefab, pos, rot, player);
    }
  }

  public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
  {
    Debug.Log($"Player left: {player}");
  }

  public void OnInput(NetworkRunner runner, NetworkInput input)
  {
    // ProvideInput = false なので今は何もしない
  }

  public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
  {
  }

  public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
  {
    Debug.Log($"Runner shutdown: {shutdownReason}");
  }

  public void OnConnectedToServer(NetworkRunner runner)
  {
    Debug.Log("Connected to server");
  }

  // ★ Fusion2 では NetDisconnectReason が追加されたシグネチャ
  public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
  {
    Debug.Log($"Disconnected from server: {reason}");
  }

  public void OnConnectRequest(
      NetworkRunner runner,
      NetworkRunnerCallbackArgs.ConnectRequest request,
      byte[] token)
  {
  }

  public void OnConnectFailed(
      NetworkRunner runner,
      NetAddress remoteAddress,
      NetConnectFailedReason reason)
  {
    Debug.LogError($"Connect failed: {reason}");
  }

  public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
  {
  }

  public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
  {
  }

  public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
  {
  }

  public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
  {
  }

  public void OnSceneLoadDone(NetworkRunner runner)
  {
  }

  public void OnSceneLoadStart(NetworkRunner runner)
  {
  }

  // ★ Fusion2 で追加された AOI コールバック
  public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
  {
  }

  public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
  {
  }

  // ★ Fusion2 で追加された Reliable Data ストリーミング系コールバック
  public void OnReliableDataReceived(
      NetworkRunner runner,
      PlayerRef player,
      ReliableKey key,
      ArraySegment<byte> data)
  {
  }

  public void OnReliableDataProgress(
      NetworkRunner runner,
      PlayerRef player,
      ReliableKey key,
      float progress)
  {
  }
}
