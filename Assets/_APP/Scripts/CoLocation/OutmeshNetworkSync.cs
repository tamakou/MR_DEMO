using Fusion;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// ★修正版：Outmesh の同期を「Shared Anchor 基準（Anchor Local）」で行う。
/// - これにより、Host/Client の Unityワールド原点が違っても、3Dモデルの位置ズレ（特にY）を抑制できる。
///
/// 使い方：
/// - このスクリプトは NetworkObject(Tracker) に付ける（FusionがSpawnする側）
/// - OutmeshRoot(ローカルで存在するモデル) は各端末に存在してOK（ネットワークSpawn不要）
///
/// オプション：
/// - allowClientToDriveViaRpc=true の場合、Clientが掴んでいる間は RPC で StateAuthority(Host) に姿勢を送って追従させる。
///   （Host/Serverモードで StateAuthority 移譲がうまく行かない場合の保険）
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
public class OutmeshNetworkSync : NetworkBehaviour
{
    [Header("Scene References (optional)")]
    [SerializeField] private SharedAnchorManager sharedAnchorManager;

    [Tooltip("OutmeshRoot を直接割り当てたい場合（未指定なら名前検索）")]
    [SerializeField] private Transform outmeshRoot;

    [Tooltip("名前検索するときの OutmeshRoot 名称")]
    [SerializeField] private string outmeshRootName = "OutmeshRoot";

    [Header("Anchor Frame")]
    [Tooltip("true: アンカーのYawのみ使用（pitch/rollノイズがYズレを生みやすいため推奨）")]
    [SerializeField] private bool useYawOnlyAnchorFrame = true;

    [Tooltip("true: この端末でアンカーがローカライズされるまで同期適用しない（ズレた座標を掴まないため推奨）")]
    [SerializeField] private bool requireLocalizedAnchor = true;

    [Header("Client control (Host stays StateAuthority)")]
    [Tooltip("true: クライアントが掴んでいる間、RPCでStateAuthorityへ姿勢を送る（Authority移譲に依存しない）")]
    [SerializeField] private bool allowClientToDriveViaRpc = true;

    [Tooltip("掴み中のRPC送信レート(Hz)")]
    [SerializeField] private float clientSendRateHz = 20f;

    [Tooltip("位置がこれ未満(m)しか変わっていなければ送らない")]
    [SerializeField] private float clientSendPosThreshold = 0.002f; // 2mm

    [Tooltip("回転がこれ未満(deg)しか変わっていなければ送らない")]
    [SerializeField] private float clientSendRotThresholdDeg = 0.5f;

    // ----------------------------------------------------

    private Transform _localOutmeshRoot;
    private XRGrabInteractable _grabInteractable;
    private bool _subscribed = false;

    private OVRSpatialAnchor _anchor;
    private bool _anchorReady = false;

    private double _nextSendTime = 0;
    private Vector3 _lastSentLocalPos;
    private Quaternion _lastSentLocalRot = Quaternion.identity;
    private bool _hasLastSent = false;
    private bool _grabbedFlag = false;  // ★イベントベースの掘みフラグ

    public override void Spawned()
    {
        TryInitializeRefs();
        TryInitializeOutmeshRoot();
        TryInitializeAnchorFromManager();
    }

    private void Awake()
    {
        if (sharedAnchorManager == null)
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();
    }

    private void OnEnable()
    {
        if (sharedAnchorManager == null)
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();

        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.OnAnchorLocalized -= HandleAnchorLocalized;
            sharedAnchorManager.OnAnchorLocalized += HandleAnchorLocalized;
        }
    }

    private void OnDisable()
    {
        if (sharedAnchorManager != null)
            sharedAnchorManager.OnAnchorLocalized -= HandleAnchorLocalized;
    }

    private void TryInitializeRefs()
    {
        if (sharedAnchorManager == null)
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();
    }

    private void TryInitializeAnchorFromManager()
    {
        if (_anchorReady) return;

        if (sharedAnchorManager != null && sharedAnchorManager.TryGetPrimaryAnchor(out var a))
        {
            HandleAnchorLocalized(a);
        }
    }

    private void HandleAnchorLocalized(OVRSpatialAnchor anchor)
    {
        if (anchor == null) return;

        // 基本は「最初のアンカー」を採用（複数ローカライズが来ても基準がブレないように）
        if (_anchor != null && _anchor.Uuid != anchor.Uuid)
        {
            Debug.LogWarning($"[OutmeshNetworkSync] Another anchor localized ({anchor.Uuid}). Keeping the first one ({_anchor.Uuid}).");
            return;
        }

        _anchor = anchor;
        _anchorReady = true;

        Debug.Log($"[OutmeshNetworkSync] Anchor localized. UUID={anchor.Uuid}, Pos={anchor.transform.position}, Rot={anchor.transform.rotation.eulerAngles}");

        // アンカーが用意できた瞬間にスナップ（見た目が一気に揃う）
        if (_localOutmeshRoot != null && Object != null)
        {
            if (Object.HasStateAuthority)
            {
                // 今のローカル outmesh の姿勢をネットワーク状態に反映（Anchor Localで）
                var (lp, lr) = WorldToAnchorLocalPose(_localOutmeshRoot.position, _localOutmeshRoot.rotation);
                transform.position = lp;
                transform.rotation = lr;
            }
            else
            {
                // ネットワーク状態をローカル outmesh に反映
                ApplyTrackerToOutmesh();
            }
        }
    }

    private void TryInitializeOutmeshRoot()
    {
        if (_localOutmeshRoot != null) return;

        if (outmeshRoot != null)
        {
            _localOutmeshRoot = outmeshRoot;
        }
        else
        {
            GameObject rootObj = GameObject.Find(outmeshRootName);
            if (rootObj != null) _localOutmeshRoot = rootObj.transform;
        }

        if (_localOutmeshRoot == null)
        {
            // まだロードされていないだけなので、Updateで再試行
            return;
        }

        Debug.Log($"[OutmeshNetworkSync] Found local OutmeshRoot: {_localOutmeshRoot.name}");

        FindAndSubscribeToGrab();

        // 初期スナップ
        if (Object != null)
        {
            if (Object.HasStateAuthority)
            {
                if (!requireLocalizedAnchor || _anchorReady)
                {
                    var (lp, lr) = WorldToAnchorLocalPose(_localOutmeshRoot.position, _localOutmeshRoot.rotation);
                    transform.position = lp;
                    transform.rotation = lr;
                }
            }
            else
            {
                if (!requireLocalizedAnchor || _anchorReady)
                {
                    ApplyTrackerToOutmesh();
                }
            }
        }
    }

    public override void FixedUpdateNetwork()
    {
        // ★FixedUpdateNetwork は StateAuthority (Host) 側でのみ確実に呼ばれる
        // Client 側は Render() で処理する

        if (_localOutmeshRoot == null) return;
        if (requireLocalizedAnchor && !_anchorReady) return;

        // StateAuthority のみ処理
        if (!Object.HasStateAuthority) return;

        bool isLocallyGrabbed = _grabbedFlag || (_grabInteractable != null && _grabInteractable.isSelected);

        // ★修正：Host が掴んでいないときは ApplyTrackerToOutmesh を呼ばない
        // 理由：アンカーのトラッキングノイズが毎フレーム座標変換に影響し、モデルが揺れる
        // Client が RPC で送ってきた場合のみ、Rpc_ClientDrivenPose で transform が更新され、
        // その値は NetworkTransform 経由で Client に返されるので、ここで適用する必要なし

        // ローカル見た目を Tracker に反映し、他へ配信
        var (lp, lr) = WorldToAnchorLocalPose(_localOutmeshRoot.position, _localOutmeshRoot.rotation);

        // ★デバッグ：掴んでいる間だけログ出力
        if (isLocallyGrabbed && Time.frameCount % 30 == 0)
        {
            Debug.Log($"[OutmeshNetworkSync] HOST GRABBING: TrackerPos={lp}, OutmeshWorldPos={_localOutmeshRoot.position}");
        }

        transform.position = lp;
        transform.rotation = lr;
    }

    /// <summary>
    /// Render() は全端末で毎フレーム呼ばれる。Client (Proxy) の処理はここで行う。
    /// </summary>
    public override void Render()
    {
        if (_localOutmeshRoot == null) return;
        if (requireLocalizedAnchor && !_anchorReady) return;

        // StateAuthority は FixedUpdateNetwork で処理済み
        if (Object.HasStateAuthority) return;

        bool isLocallyGrabbed = _grabbedFlag || (_grabInteractable != null && _grabInteractable.isSelected);

        // ★デバッグ：Client の状態を毎秒ログ
        if (Time.frameCount % 60 == 0)
        {
            var (wp, wr) = AnchorLocalToWorldPose(transform.position, transform.rotation);
            Debug.Log($"[OutmeshNetworkSync] CLIENT RENDER: Grabbed={isLocallyGrabbed}, TrackerPos={transform.position}, ConvertedWorldPos={wp}");
        }

        if (!isLocallyGrabbed)
        {
            // 掴んでいないときは、ネットワーク状態をローカルに反映
            ApplyTrackerToOutmesh();
        }
        else if (allowClientToDriveViaRpc)
        {
            // 掴んでいるときは、ローカル状態を Host に送信
            Debug.Log($"[OutmeshNetworkSync] CLIENT CALLING TrySend...");
            TrySendGrabbedPoseToStateAuthority();
        }
    }

    private void TrySendGrabbedPoseToStateAuthority(bool force = false)
    {
        if (Runner == null || !Runner.IsRunning)
        {
            Debug.LogWarning("[OutmeshNetworkSync] TrySend: Runner null or not running");
            return;
        }

        double now = Time.timeAsDouble;
        double interval = (clientSendRateHz <= 0f) ? 0.0 : 1.0 / clientSendRateHz;

        if (!force && now < _nextSendTime)
        {
            // レート制限（デバッグ用にたまにログ）
            if (Time.frameCount % 30 == 0)
                Debug.Log($"[OutmeshNetworkSync] TrySend: rate limited now={now:F2} next={_nextSendTime:F2}");
            return;
        }

        var (lp, lr) = WorldToAnchorLocalPose(_localOutmeshRoot.position, _localOutmeshRoot.rotation);

        if (!force && _hasLastSent)
        {
            float dp = Vector3.Distance(_lastSentLocalPos, lp);
            float da = Quaternion.Angle(_lastSentLocalRot, lr);
            if (dp < clientSendPosThreshold && da < clientSendRotThresholdDeg)
            {
                // 変化なし（デバッグ用にたまにログ）
                if (Time.frameCount % 30 == 0)
                    Debug.Log($"[OutmeshNetworkSync] TrySend: below threshold dp={dp:F4} da={da:F3}");
                _nextSendTime = now + interval;
                return;
            }
        }

        _lastSentLocalPos = lp;
        _lastSentLocalRot = lr;
        _hasLastSent = true;
        _nextSendTime = now + interval;

        Debug.Log($"[OutmeshNetworkSync] SEND Rpc_ClientDrivenPose lp={lp} lr={lr.eulerAngles}");
        Rpc_ClientDrivenPose(lp, lr);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void Rpc_ClientDrivenPose(Vector3 anchorLocalPos, Quaternion anchorLocalRot, RpcInfo info = default)
    {
        Debug.Log($"[OutmeshNetworkSync] RECV Rpc_ClientDrivenPose from={info.Source} pos={anchorLocalPos}");
        transform.position = anchorLocalPos;
        transform.rotation = anchorLocalRot;

        // ★追加：Host が Client の操作を受け取ったら、即座にローカル見た目にも反映
        if (_localOutmeshRoot != null)
        {
            ApplyTrackerToOutmesh();
        }
    }

    private void ApplyTrackerToOutmesh()
    {
        var (wp, wr) = AnchorLocalToWorldPose(transform.position, transform.rotation);
        _localOutmeshRoot.SetPositionAndRotation(wp, wr);
    }

    private (Vector3 localPos, Quaternion localRot) WorldToAnchorLocalPose(Vector3 worldPos, Quaternion worldRot)
    {
        GetAnchorFrame(out var originPos, out var originRot);

        Vector3 localPos = Quaternion.Inverse(originRot) * (worldPos - originPos);
        Quaternion localRot = Quaternion.Inverse(originRot) * worldRot;
        return (localPos, localRot);
    }

    private (Vector3 worldPos, Quaternion worldRot) AnchorLocalToWorldPose(Vector3 localPos, Quaternion localRot)
    {
        GetAnchorFrame(out var originPos, out var originRot);

        Vector3 worldPos = originPos + originRot * localPos;
        Quaternion worldRot = originRot * localRot;
        return (worldPos, worldRot);
    }

    private void GetAnchorFrame(out Vector3 originPos, out Quaternion originRot)
    {
        if (_anchor != null)
        {
            var t = _anchor.transform;
            originPos = t.position;

            if (useYawOnlyAnchorFrame)
                originRot = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
            else
                originRot = t.rotation;

            return;
        }

        originPos = Vector3.zero;
        originRot = Quaternion.identity;
    }

    private void Update()
    {
        if (_localOutmeshRoot == null)
        {
            TryInitializeOutmeshRoot();
        }

        if (!_anchorReady)
        {
            TryInitializeAnchorFromManager();
        }

        if (_localOutmeshRoot != null && !_subscribed)
        {
            FindAndSubscribeToGrab();
        }
    }

    private void FindAndSubscribeToGrab()
    {
        if (_localOutmeshRoot == null) return;

        if (_grabInteractable == null)
        {
            _grabInteractable = _localOutmeshRoot.GetComponentInChildren<XRGrabInteractable>();
        }

        if (_grabInteractable != null && !_subscribed)
        {
            _grabInteractable.selectEntered.AddListener(OnGrabbed);
            _grabInteractable.selectExited.AddListener(OnReleased);
            _subscribed = true;
            Debug.Log("[OutmeshNetworkSync] Subscribed to XRGrabInteractable events on local object.");
        }
    }

    private void OnDestroy()
    {
        if (_grabInteractable != null)
        {
            _grabInteractable.selectEntered.RemoveListener(OnGrabbed);
            _grabInteractable.selectExited.RemoveListener(OnReleased);
        }
    }

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        _grabbedFlag = true;

        // allowClientToDriveViaRpc=false のときは従来通り Authority 移譲を狙う
        if (!allowClientToDriveViaRpc && !Object.HasStateAuthority)
        {
            Debug.Log("[OutmeshNetworkSync] Object grabbed. Requesting State Authority...");
            Object.RequestStateAuthority();
        }

        // RPCレート制御をリセット（掴んだ瞬間に即送る）
        _nextSendTime = 0;
        _hasLastSent = false;

        Debug.Log($"[OutmeshNetworkSync] Grabbed. SA={Object.HasStateAuthority} allowRpc={allowClientToDriveViaRpc}");

        // ★追加：Clientが掴んだ瞬間、必ず1回はHost(StateAuthority)へ送る
        if (allowClientToDriveViaRpc && Object != null && !Object.HasStateAuthority)
        {
            Debug.Log("[OutmeshNetworkSync] Grabbed: force send first pose");
            TrySendGrabbedPoseToStateAuthority(force: true);
        }
    }

    private void OnReleased(SelectExitEventArgs args)
    {
        _grabbedFlag = false;
        _nextSendTime = 0;
        _hasLastSent = false;

        Debug.Log($"[OutmeshNetworkSync] Released. SA={Object.HasStateAuthority}");
    }
}
