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
/// </summary>
public class ColocationNetworkManager : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private NetworkRunner runnerPrefab;
    [SerializeField] private NetworkObject playerPrefab;
    [SerializeField] private NetworkObject outmeshTrackerPrefab;
    [SerializeField] private SharedAnchorManager sharedAnchorManager;
    [SerializeField] private AnchorPlacementController placementController;

    /// <summary> アンカー UUID（デバッグ用）。共有自体は groupUuid で行う。 </summary>
    [Networked] public NetworkString<_64> AnchorUuid { get; set; }

    /// <summary> Shared Spatial Anchors のグループ UUID（ホスト生成 → 全クライアントに同期） </summary>
    [Networked] public NetworkString<_64> AnchorGroupUuid { get; set; }

    private NetworkRunner _localRunner;
    private ChangeDetector _changeDetector;

    private Guid _groupUuid;
    private bool _hasGroupUuid;

    private void Awake()
    {
        if (sharedAnchorManager == null)
        {
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();
        }
    }

    /// <summary>
    /// ホスト起動。
    /// </summary>
    public async void StartHost()
    {
        if (_localRunner == null) _localRunner = Instantiate(runnerPrefab);

        var sceneManager = _localRunner.GetComponent<NetworkSceneManagerDefault>();
        if (sceneManager == null)
        {
            sceneManager = _localRunner.gameObject.AddComponent<NetworkSceneManagerDefault>();
        }

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

        // グループUUIDの生成とネットワーク同期は Spawned() で行う
        // （NetworkObject が Attached になるまで Networked Propertyは書き込めない）

        // SharedAnchorManager から「保存＋共有完了したアンカー UUID」を受け取る
        sharedAnchorManager.OnAnchorCreated -= OnAnchorCreatedByHost;
        sharedAnchorManager.OnAnchorCreated += OnAnchorCreatedByHost;

        // アンカーの配置モード開始
        if (placementController == null)
        {
            placementController = FindFirstObjectByType<AnchorPlacementController>();
            if (placementController == null)
            {
                placementController = gameObject.AddComponent<AnchorPlacementController>();
            }
        }

        placementController.OnConfirmed -= OnAnchorPlacementConfirmed;
        placementController.OnConfirmed += OnAnchorPlacementConfirmed;
        placementController.BeginPlacement();
    }

    /// <summary> クライアント起動。 </summary>
    public async void StartClient()
    {
        if (_localRunner == null) _localRunner = Instantiate(runnerPrefab);

        var sceneManager = _localRunner.GetComponent<NetworkSceneManagerDefault>();
        if (sceneManager == null)
        {
            sceneManager = _localRunner.gameObject.AddComponent<NetworkSceneManagerDefault>();
        }

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
        {
            Debug.Log("[ColocationNetworkManager] Client Started");
        }
        else
        {
            Debug.LogError($"[ColocationNetworkManager] Failed to start Client: {result.ShutdownReason}");
        }
    }

    public override void Spawned()
    {
        _changeDetector = GetChangeDetector(ChangeDetector.Source.SimulationState);

        // ローカルプレイヤーを Spawn（任意）
        if (Runner.LocalPlayer != null && playerPrefab != null)
        {
            Runner.Spawn(playerPrefab, Vector3.zero, Quaternion.identity, Runner.LocalPlayer);
        }

        // Host が Outmesh Tracker を一度だけ Spawn
        if (Runner.IsServer && outmeshTrackerPrefab != null)
        {
            Debug.Log("[ColocationNetworkManager] Spawning Outmesh Tracker...");
            Runner.Spawn(outmeshTrackerPrefab, Vector3.zero, Quaternion.identity);
        }

        // ホスト側: グループUUIDを生成してネットワークに同期
        // Spawned() で行うことで NetworkObject が Attached 状態であることを保証
        if (Runner.IsServer && !_hasGroupUuid)
        {
            _groupUuid = Guid.NewGuid();
            _hasGroupUuid = true;
            AnchorGroupUuid = _groupUuid.ToString();
            Debug.Log($"[ColocationNetworkManager] Generated group UUID in Spawned: {_groupUuid}");

            if (sharedAnchorManager != null)
            {
                sharedAnchorManager.SetGroupUuid(_groupUuid);
            }
        }

        // クライアントが途中参加したとき：
        if (!Runner.IsServer)
        {
            TryInitGroupFromNetwork();

            string groupStr = AnchorGroupUuid.ToString();
            string anchorStr = AnchorUuid.ToString();

            if (_hasGroupUuid && !string.IsNullOrEmpty(anchorStr))
            {
                Debug.Log($"[ColocationNetworkManager] Late-join client found group {groupStr} and anchor {anchorStr}. Loading shared anchors…");
                sharedAnchorManager.LoadAnchorsForGroup(_groupUuid);
            }
        }
    }

    public override void Render()
    {
        if (_changeDetector == null) return;

        foreach (var change in _changeDetector.DetectChanges(this))
        {
            if (change == nameof(AnchorUuid))
            {
                OnAnchorUuidChanged();
            }
            else if (change == nameof(AnchorGroupUuid))
            {
                OnAnchorGroupUuidChanged();
            }
        }
    }

    /// <summary>
    /// ホスト側：SharedAnchorManager からの通知。
    /// SaveAnchorAsync + ShareAsync まで成功したアンカーの UUID が来る。
    /// </summary>
    private void OnAnchorCreatedByHost(Guid uuid)
    {
        Debug.Log($"[ColocationNetworkManager] Host created & shared anchor. Setting Networked UUID: {uuid}");
        AnchorUuid = uuid.ToString();
    }

    /// <summary>
    /// クライアント側：AnchorUuid が更新されたとき（＝ホストが共有完了した合図）。
    /// </summary>
    private void OnAnchorUuidChanged()
    {
        if (Runner.IsServer) return; // ホスト側では何もしない

        TryInitGroupFromNetwork();

        if (!_hasGroupUuid)
        {
            Debug.LogWarning("[ColocationNetworkManager] AnchorUuid changed but group UUID is not initialized yet.");
            return;
        }

        string uuidStr = AnchorUuid.ToString();
        Debug.Log($"[ColocationNetworkManager] Anchor UUID Changed: {uuidStr}. Requesting shared anchors for group {_groupUuid}.");

        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.LoadAnchorsForGroup(_groupUuid);
        }
    }

    /// <summary>
    /// クライアント側：AnchorGroupUuid が同期されたとき。
    /// （ここではロードは行わず、UUID の初期化だけをする）
    /// </summary>
    private void OnAnchorGroupUuidChanged()
    {
        if (Runner.IsServer) return;
        TryInitGroupFromNetwork();
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
            {
                sharedAnchorManager.SetGroupUuid(_groupUuid);
            }
        }
    }

    /// <summary>
    /// ユーザーがアンカー位置を確定したときに呼ばれる。
    /// ここを async にして CreateAnchor の完了を正しく待つ。
    /// </summary>
    private async void OnAnchorPlacementConfirmed(Vector3 pos, Quaternion rot)
    {
        Debug.Log($"[ColocationNetworkManager] Placement Confirmed. Creating Anchor at {pos}");

        if (placementController != null)
        {
            placementController.OnConfirmed -= OnAnchorPlacementConfirmed;
        }

        if (!_hasGroupUuid)
        {
            Debug.LogWarning("[ColocationNetworkManager] Group UUID is not set when creating anchor. Generating one locally.");
            _groupUuid = Guid.NewGuid();
            _hasGroupUuid = true;
            AnchorGroupUuid = _groupUuid.ToString();

            if (sharedAnchorManager != null)
            {
                sharedAnchorManager.SetGroupUuid(_groupUuid);
            }
        }

        var anchor = await sharedAnchorManager.CreateAnchor(pos, rot);

        if (anchor == null)
        {
            Debug.LogError("[ColocationNetworkManager] Anchor creation or sharing failed after placement.");
        }
        // AnchorUuid の同期は SharedAnchorManager.OnAnchorCreated → OnAnchorCreatedByHost で行われる。
    }
}
