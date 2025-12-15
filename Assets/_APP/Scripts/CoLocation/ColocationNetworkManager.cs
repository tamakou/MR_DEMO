using Fusion;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Photon Fusion を使ったネットワーク管理と、Shared Spatial Anchors との橋渡し。
/// v71 以降の Shared Spatial Anchors では、
///   - ホストが「グループ UUID」を生成
///   - SaveAnchorAsync() で保存
///   - ShareAsync(anchors, groupUuid) でクラウド共有
///   - クライアントは LoadUnboundSharedAnchorsAsync(groupUuid, ...) でロード
/// というフローなので、ここで groupUuid を Networked プロパティとして同期する。
///
/// ★修正点：
/// - AnchorUuid / AnchorGroupUuid の到着順に依存しないよう、両方から TryLoadSharedAnchorsIfReady() を呼ぶ
/// - playerPrefab の全端末 Spawn を削除（Fusion Host モードでは Server のみが Spawn 可能）
/// - OnDestroy でイベント登録解除を追加
/// </summary>
public class ColocationNetworkManager : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private NetworkRunner runnerPrefab;
    [SerializeField] private NetworkObject playerPrefab;            // ※任意：本スクリプト内ではSpawnしない
    [SerializeField] private NetworkObject outmeshTrackerPrefab;
    [SerializeField] private SharedAnchorManager sharedAnchorManager;
    [SerializeField] private AnchorPlacementController placementController;

    /// <summary> アンカー UUID（デバッグ用）。共有自体は groupUuid で行うが、"共有完了"の合図にも使う。 </summary>
    [Networked] public NetworkString<_64> AnchorUuid { get; set; }

    /// <summary> Shared Spatial Anchors のグループ UUID（ホスト生成 → 全クライアントに同期） </summary>
    [Networked] public NetworkString<_64> AnchorGroupUuid { get; set; }

    private NetworkRunner _localRunner;
    private ChangeDetector _changeDetector;

    private Guid _groupUuid;
    private bool _hasGroupUuid;

    [Header("Client Anchor Load")]
    [Tooltip("同じフレームで複数回Loadが走るのを避けるためのクールダウン秒数")]
    [SerializeField] private float anchorLoadCooldownSeconds = 0.5f;
    private float _lastAnchorLoadAttemptTime = -999f;

    private void Awake()
    {
        if (sharedAnchorManager == null)
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();
    }

    public async void StartHost()
    {
        if (_localRunner == null) _localRunner = Instantiate(runnerPrefab);

        var sceneManager = _localRunner.GetComponent<NetworkSceneManagerDefault>();
        if (sceneManager == null) sceneManager = _localRunner.gameObject.AddComponent<NetworkSceneManagerDefault>();

        var sceneRef = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex);
        var sceneInfo = new NetworkSceneInfo();
        sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Single);

        var result = await _localRunner.StartGame(new StartGameArgs()
        {
            GameMode = GameMode.Host,
            SessionName = "CoLocationRoom",
            Scene = sceneInfo,
            SceneManager = sceneManager
        });

        if (!result.Ok)
        {
            Debug.LogError($"[ColocationNetworkManager] Failed to start Host: {result.ShutdownReason}");
            return;
        }

        Debug.Log("[ColocationNetworkManager] Host Started");

        // SharedAnchorManager から「保存＋共有完了したアンカー UUID」を受け取る
        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.OnAnchorCreated -= OnAnchorCreatedByHost;
            sharedAnchorManager.OnAnchorCreated += OnAnchorCreatedByHost;
        }

        // アンカー配置モード開始
        if (placementController == null)
        {
            placementController = FindFirstObjectByType<AnchorPlacementController>();
            if (placementController == null)
                placementController = gameObject.AddComponent<AnchorPlacementController>();
        }

        placementController.OnConfirmed -= OnAnchorPlacementConfirmed;
        placementController.OnConfirmed += OnAnchorPlacementConfirmed;
        placementController.BeginPlacement();
    }

    public async void StartClient()
    {
        if (_localRunner == null) _localRunner = Instantiate(runnerPrefab);

        var sceneManager = _localRunner.GetComponent<NetworkSceneManagerDefault>();
        if (sceneManager == null) sceneManager = _localRunner.gameObject.AddComponent<NetworkSceneManagerDefault>();

        var sceneRef = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex);
        var sceneInfo = new NetworkSceneInfo();
        sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Single);

        var result = await _localRunner.StartGame(new StartGameArgs()
        {
            GameMode = GameMode.Client,
            SessionName = "CoLocationRoom",
            Scene = sceneInfo,
            SceneManager = sceneManager
        });

        if (result.Ok)
            Debug.Log("[ColocationNetworkManager] Client Started");
        else
            Debug.LogError($"[ColocationNetworkManager] Failed to start Client: {result.ShutdownReason}");
    }

    public override void Spawned()
    {
        _changeDetector = GetChangeDetector(ChangeDetector.Source.SimulationState);

        // Host が Outmesh Tracker を一度だけ Spawn
        if (Runner.IsServer && outmeshTrackerPrefab != null)
        {
            Debug.Log("[ColocationNetworkManager] Spawning Outmesh Tracker...");
            Runner.Spawn(outmeshTrackerPrefab, Vector3.zero, Quaternion.identity);
        }

        // ホスト側：グループUUID生成 → ネットワーク同期
        if (Runner.IsServer && !_hasGroupUuid)
        {
            _groupUuid = Guid.NewGuid();
            _hasGroupUuid = true;
            AnchorGroupUuid = _groupUuid.ToString();

            Debug.Log($"[ColocationNetworkManager] Generated group UUID in Spawned: {_groupUuid}");

            if (sharedAnchorManager != null)
                sharedAnchorManager.SetGroupUuid(_groupUuid);
        }

        // クライアント：遅延参加時は「両方そろっていたらロード」を試す
        if (!Runner.IsServer)
        {
            TryLoadSharedAnchorsIfReady("Spawned (late join)");
        }
    }

    public override void Render()
    {
        if (_changeDetector == null) return;

        foreach (var change in _changeDetector.DetectChanges(this))
        {
            if (change == nameof(AnchorUuid))
            {
                TryLoadSharedAnchorsIfReady("AnchorUuid changed");
            }
            else if (change == nameof(AnchorGroupUuid))
            {
                TryLoadSharedAnchorsIfReady("AnchorGroupUuid changed");
            }
        }
    }

    private void OnAnchorCreatedByHost(Guid uuid)
    {
        Debug.Log($"[ColocationNetworkManager] Host created & shared anchor. Setting Networked UUID: {uuid}");
        AnchorUuid = uuid.ToString();
    }

    /// <summary>
    /// Networked な AnchorGroupUuid からローカルの _groupUuid / SharedAnchorManager を初期化。
    /// </summary>
    private void TryInitGroupFromNetwork()
    {
        if (_hasGroupUuid) return;

        string groupStr = AnchorGroupUuid.ToString();
        if (!string.IsNullOrEmpty(groupStr) && Guid.TryParse(groupStr, out var guid))
        {
            _groupUuid = guid;
            _hasGroupUuid = true;

            Debug.Log($"[ColocationNetworkManager] Group UUID initialized from network: {_groupUuid}");

            if (sharedAnchorManager != null)
                sharedAnchorManager.SetGroupUuid(_groupUuid);
        }
    }

    /// <summary>
    /// クライアント側で「groupUuid と anchorUuid が揃ったらロードする」統一ロジック。
    /// どちらが先に到着しても、両方揃った時点でロードが走る。
    /// </summary>
    private void TryLoadSharedAnchorsIfReady(string reason)
    {
        if (Runner == null || Runner.IsServer) return;

        if (sharedAnchorManager == null)
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();

        TryInitGroupFromNetwork();
        if (!_hasGroupUuid) return;

        string anchorStr = AnchorUuid.ToString();
        if (string.IsNullOrEmpty(anchorStr)) return; // 共有完了前はロードしない

        // クールダウンチェック（連続呼び出し防止）
        if (anchorLoadCooldownSeconds > 0f &&
            Time.unscaledTime - _lastAnchorLoadAttemptTime < anchorLoadCooldownSeconds)
        {
            return;
        }
        _lastAnchorLoadAttemptTime = Time.unscaledTime;

        Debug.Log($"[ColocationNetworkManager] {reason}. Loading shared anchors for group {_groupUuid} (anchor={anchorStr})…");
        sharedAnchorManager?.LoadAnchorsForGroup(_groupUuid);
    }

    private async void OnAnchorPlacementConfirmed(Vector3 pos, Quaternion rot)
    {
        Debug.Log($"[ColocationNetworkManager] Placement Confirmed. Creating Anchor at {pos}");

        if (placementController != null)
            placementController.OnConfirmed -= OnAnchorPlacementConfirmed;

        if (!_hasGroupUuid)
        {
            // 念のため（通常はSpawnedで設定済み）
            _groupUuid = Guid.NewGuid();
            _hasGroupUuid = true;
            AnchorGroupUuid = _groupUuid.ToString();

            if (sharedAnchorManager != null)
                sharedAnchorManager.SetGroupUuid(_groupUuid);
        }

        var anchor = await sharedAnchorManager.CreateAnchor(pos, rot);
        if (anchor == null)
        {
            Debug.LogError("[ColocationNetworkManager] Anchor creation or sharing failed after placement.");
        }
        // AnchorUuid の同期は SharedAnchorManager.OnAnchorCreated → OnAnchorCreatedByHost で行われる
    }

    private void OnDestroy()
    {
        if (sharedAnchorManager != null)
            sharedAnchorManager.OnAnchorCreated -= OnAnchorCreatedByHost;

        if (placementController != null)
            placementController.OnConfirmed -= OnAnchorPlacementConfirmed;
    }
}
