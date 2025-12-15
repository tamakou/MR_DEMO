using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Shared Spatial Anchor の作成・保存・共有・読み込みを担当するマネージャ。
/// v71 以降で推奨されている「グループ共有」API
///   - SaveAnchorAsync()
///   - OVRSpatialAnchor.ShareAsync(anchors, groupUuid)
///   - OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, list)
/// を使う実装。
///
/// ★修正点：
/// - OutmeshNetworkSync など別コンポーネントから「現在ローカライズ済みアンカー」を参照できるように
///   PrimaryAnchor / LastLocalizedAnchor / TryGetPrimaryAnchor を追加。
/// </summary>
public class SharedAnchorManager : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private GameObject anchorPrefab;

    /// <summary> アンカーが「保存＋共有」まで完了したとき（UUID をネットワークに流す用） </summary>
    public Action<Guid> OnAnchorCreated;

    /// <summary> アンカーがローカライズされ、シーン上の OVRSpatialAnchor にバインドされたとき </summary>
    public Action<OVRSpatialAnchor> OnAnchorLocalized;

    /// <summary> ユーザーへの通知メッセージ（エラーや操作指示など） </summary>
    public Action<string> OnStatusMessage;

    private OVRSpatialAnchor _localAnchor;

    /// <summary> このセッションで使用する SSA グループ UUID（ホストが生成して全クライアントに共有） </summary>
    private Guid? _currentGroupUuid;
    public Guid? CurrentGroupUuid => _currentGroupUuid;

    // -------------------- ★追加：ローカライズ済みアンカー参照（他スクリプト用） --------------------
    /// <summary> このセッションで「基準にする」アンカー（通常は最初にローカライズしたもの） </summary>
    public OVRSpatialAnchor PrimaryAnchor { get; private set; }

    /// <summary> 直近にローカライズされたアンカー（複数来た場合の最新） </summary>
    public OVRSpatialAnchor LastLocalizedAnchor { get; private set; }

    /// <summary>
    /// OutmeshNetworkSync などが「基準アンカー」を取得するためのAPI。
    /// </summary>
    public bool TryGetPrimaryAnchor(out OVRSpatialAnchor anchor)
    {
        anchor = PrimaryAnchor != null ? PrimaryAnchor : LastLocalizedAnchor;
        return anchor != null;
    }

    private void RegisterLocalizedAnchor(OVRSpatialAnchor anchor)
    {
        if (anchor == null) return;

        LastLocalizedAnchor = anchor;

        if (PrimaryAnchor == null)
        {
            PrimaryAnchor = anchor;
            Debug.Log($"[SharedAnchorManager] PrimaryAnchor set. UUID={anchor.Uuid}");
        }
        else if (PrimaryAnchor.Uuid != anchor.Uuid)
        {
            Debug.LogWarning($"[SharedAnchorManager] Multiple anchors localized. Primary stays {PrimaryAnchor.Uuid}, new={anchor.Uuid}");
        }
    }
    // ---------------------------------------------------------------------------------------------

    /// <summary> ColocationNetworkManager からグループ UUID を設定してもらう </summary>
    public void SetGroupUuid(Guid groupUuid)
    {
        _currentGroupUuid = groupUuid;
        Debug.Log($"[SharedAnchorManager] Group UUID set to {_currentGroupUuid}");
    }

    /// <summary>
    /// アンカー作成フロー：
    ///   Instantiate → WhenLocalizedAsync → SaveAnchorAsync → (必要なら) ShareAsync(group)
    /// 成功したら OnAnchorLocalized は必ず呼び、OnAnchorCreated は「共有まで成功した場合」に発火。
    /// </summary>
    public async Task<OVRSpatialAnchor> CreateAnchor(Vector3? position = null, Quaternion? rotation = null)
    {
        if (anchorPrefab == null)
        {
            Debug.LogError("[SharedAnchorManager] Anchor Prefab is not assigned!");
            OnStatusMessage?.Invoke("内部エラー：アンカー用プレハブが設定されていません。");
            return null;
        }

        Vector3 pos;
        Quaternion rot;

        if (position.HasValue && rotation.HasValue)
        {
            pos = position.Value;
            rot = rotation.Value;
        }
        else
        {
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[SharedAnchorManager] No Camera.main – cannot compute default anchor pose.");
                OnStatusMessage?.Invoke("カメラが見つかりません。アプリの状態を確認してください。");
                return null;
            }

            pos = cam.transform.position + cam.transform.forward * 0.5f;
            rot = Quaternion.Euler(0, cam.transform.eulerAngles.y, 0);
        }

        Debug.Log($"[SharedAnchorManager] Instantiating anchor at {pos} / {rot.eulerAngles}");

        var go = Instantiate(anchorPrefab, pos, rot);
        go.name = "SharedAnchor_Host";

        var anchor = go.GetComponent<OVRSpatialAnchor>() ?? go.AddComponent<OVRSpatialAnchor>();

        // --- ローカライズ待ち ---
        Debug.Log("[SharedAnchorManager] Waiting for anchor localization...");
        OnStatusMessage?.Invoke("アンカーを作成中です。\n周囲をゆっくり見回して、空間を認識させてください。");

        bool localized = await anchor.WhenLocalizedAsync();
        if (!localized)
        {
            string msg = "アンカーのローカライズに失敗しました。\n" +
                         "アンカー付近を中心に、部屋をもう少しゆっくり見回してください。";
            Debug.LogError($"[SharedAnchorManager] {msg}");
            OnStatusMessage?.Invoke(msg);

            Destroy(go);
            return null;
        }

        Debug.Log($"[SharedAnchorManager] Anchor created & localized. UUID={anchor.Uuid}");

        // --- 永続化 ---
        var saveResult = await anchor.SaveAnchorAsync();  // OVRResult<OVRAnchor.SaveResult>
        if (!saveResult.Success)
        {
            var status = saveResult.Status;
            string msg;

            switch (status)
            {
                case OVRAnchor.SaveResult.FailureInsufficientView:
                    msg = "部屋の特徴点が不足しているため、アンカーを保存できませんでした。\n" +
                          "アンカーのある周辺を、もう少しゆっくり見回してください。";
                    break;

                case OVRAnchor.SaveResult.FailureTooDark:
                    msg = "環境が暗すぎるため、アンカーを保存できませんでした。\n" +
                          "照明を明るくして、再度お試しください。";
                    break;

                case OVRAnchor.SaveResult.FailureTooBright:
                    msg = "環境が明るすぎるため、アンカーを保存できませんでした。\n" +
                          "直射日光などを避けて、再度お試しください。";
                    break;

                case OVRAnchor.SaveResult.FailurePermissionInsufficient:
                    msg = "空間データの権限が不足しています。\n" +
                          "デバイス設定で『空間データ』『拡張空間サービス』への許可を確認してください。";
                    break;

                case OVRAnchor.SaveResult.FailureStorageAtCapacity:
                    msg = "アンカー用のストレージ容量が不足しています。\n" +
                          "不要なアンカーやアプリを削除してから再度お試しください。";
                    break;

                case OVRAnchor.SaveResult.FailureRateLimited:
                    msg = "短時間にアンカー処理を実行しすぎています。\n" +
                          "数秒待ってから再度お試しください。";
                    break;

                default:
                    msg = $"アンカー保存に失敗しました。\n理由: {status}";
                    break;
            }

            Debug.LogError($"[SharedAnchorManager] SaveAnchorAsync failed. SaveResult={status}");
            OnStatusMessage?.Invoke(msg);
            Destroy(go);  // ★修正：保存失敗時もGameObjectを破棄
            return null;
        }

        Debug.Log("[SharedAnchorManager] Anchor saved to persistent storage.");

        _localAnchor = anchor;

        // ★追加：他コンポーネントが参照できるよう登録
        RegisterLocalizedAnchor(anchor);

        // ホスト側はここで即アライン（共有に失敗してもローカル表示は可能）
        OnAnchorLocalized?.Invoke(anchor);

        // --- グループ共有（SSA の核心） ---
        if (_currentGroupUuid.HasValue)
        {
            var anchors = new List<OVRSpatialAnchor> { anchor };

            Debug.Log($"[SharedAnchorManager] Sharing anchor to group {_currentGroupUuid.Value}...");
            var shareResult = await OVRSpatialAnchor.ShareAsync(anchors, _currentGroupUuid.Value);

            if (!shareResult.Success)
            {
                var status = shareResult.Status;
                string msg = $"アンカーの共有に失敗しました。\nエラーコード: {status}";

                Debug.LogError($"[SharedAnchorManager] ShareAsync failed. Status={status}");
                OnStatusMessage?.Invoke(msg);

                // ローカルでは使えるので anchor 自体は返すが、
                // 他デバイスとは共有できていない。OnAnchorCreated は発火しない。
                return anchor;
            }

            Debug.Log($"[SharedAnchorManager] Anchor shared to group {_currentGroupUuid.Value}.");
            OnStatusMessage?.Invoke("アンカーの保存と共有に成功しました。\nクライアントが参加するのを待っています。");

            // 共有まで成功した場合のみ発火（クライアントがロードできる状態）
            OnAnchorCreated?.Invoke(anchor.Uuid);
        }
        else
        {
            // グループUUIDが未設定 = 共有できない状態
            Debug.LogWarning("[SharedAnchorManager] Group UUID not set. Anchor will not be shared with other devices.");
            OnStatusMessage?.Invoke("警告：グループUUIDが未設定のため、アンカーは共有されませんでした。\nローカルでのみ使用可能です。");
            // OnAnchorCreated は発火しない（クライアントへの通知をしない）
        }

        return anchor;
    }

    /// <summary>
    /// グループ UUID を使って Shared Spatial Anchors をロードする（クライアント側のメイン経路）。
    /// </summary>
    public async void LoadAnchorsForGroup(Guid groupUuid)
    {
        _currentGroupUuid = groupUuid;

        Debug.Log($"[SharedAnchorManager] Loading shared anchors for group: {groupUuid}");
        OnStatusMessage?.Invoke("ホストのアンカーをクラウドから取得しています…\n周囲をゆっくり見回してください。");

        var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();

        var result = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);

        if (!result.Success)
        {
            var status = result.Status;
            string msg = BuildOperationResultMessage(
                "アンカーの取得に失敗しました。",
                status);

            Debug.LogError($"[SharedAnchorManager] LoadUnboundSharedAnchorsAsync failed. Status={status}");
            OnStatusMessage?.Invoke(msg);
            return;
        }

        var loaded = result.Value;

        if (loaded == null || loaded.Count == 0)
        {
            string msg =
                "共有アンカーが見つかりませんでした (0件)。\n" +
                "・ホストがアンカーの共有をまだ完了していない\n" +
                "・クラウド同期が完了していない\n" +
                "といった可能性があります。数秒待ってから再度 Join を押してください。";
            Debug.LogWarning($"[SharedAnchorManager] {msg}");
            OnStatusMessage?.Invoke(msg);
            return;
        }

        Debug.Log($"[SharedAnchorManager] Loaded {loaded.Count} shared unbound anchor(s). Localizing...");
        OnStatusMessage?.Invoke("アンカーをローカライズしています…");

        foreach (var unbound in loaded)
        {
            await LocalizeAnchor(unbound);
        }
    }

    /// <summary>
    /// （オプション）単一デバイス内で UUID を指定してロードしたい場合用。
    /// マルチプレイでは基本的に使わず、LoadAnchorsForGroup() を使う。
    /// </summary>
    public async void LoadAnchorByUuid(Guid anchorUuid)
    {
        Debug.Log($"[SharedAnchorManager] Attempting to load anchor directly by UUID: {anchorUuid}");

        var uuids = new[] { anchorUuid };
        var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();

        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(uuids, unboundAnchors);

        if (!result.Success)
        {
            Debug.LogWarning($"[SharedAnchorManager] LoadUnboundAnchorsAsync failed. Status={result.Status}");
            return;
        }

        var loaded = result.Value;
        if (loaded == null || loaded.Count == 0)
        {
            Debug.LogWarning("[SharedAnchorManager] No anchors found for given UUID in direct load.");
            return;
        }

        foreach (var unbound in loaded)
        {
            await LocalizeAnchor(unbound);
        }
    }

    /// <summary>
    /// UnboundAnchor を LocalizeAsync → Pose を取得 → Prefab+OVRSpatialAnchor に BindTo.
    /// </summary>
    private async Task LocalizeAnchor(OVRSpatialAnchor.UnboundAnchor unboundAnchor)
    {
        bool localized = await unboundAnchor.LocalizeAsync(0); // timeout=0 は無制限
        if (!localized)
        {
            string msg = $"アンカー {unboundAnchor.Uuid} のローカライズに失敗しました。\n" +
                         "ホストのアンカー付近を中心に、周囲をもう少し見回してください。";
            Debug.LogError($"[SharedAnchorManager] {msg}");
            OnStatusMessage?.Invoke(msg);
            return;
        }

        if (!unboundAnchor.TryGetPose(out var pose))
        {
            string msg = $"アンカー {unboundAnchor.Uuid} の姿勢取得に失敗しました。\n" +
                         "トラッキングが安定していない可能性があります。少し待ってから再試行してください。";
            Debug.LogError($"[SharedAnchorManager] {msg}");
            OnStatusMessage?.Invoke(msg);
            return;
        }

        var go = Instantiate(anchorPrefab, pose.position, pose.rotation);
        go.name = $"SharedAnchor_{unboundAnchor.Uuid}";

        var anchor = go.GetComponent<OVRSpatialAnchor>() ?? go.AddComponent<OVRSpatialAnchor>();
        unboundAnchor.BindTo(anchor);   // Localized なアンカーを OVRSpatialAnchor にバインド

        Debug.Log($"[SharedAnchorManager] Anchor localized & bound. UUID={anchor.Uuid}, Pos={pose.position}, Rot={pose.rotation.eulerAngles}");

        // ★追加：他コンポーネントが参照できるよう登録
        RegisterLocalizedAnchor(anchor);

        OnStatusMessage?.Invoke("アンカーのローカライズに成功しました。");
        OnAnchorLocalized?.Invoke(anchor);
    }

    /// <summary>
    /// OperationResult（共有／ロード共通）のエラーコードを、人間向けメッセージに変換。
    /// Shared Spatial Anchors トラブルシューティングガイドに基づく。
    /// </summary>
    private string BuildOperationResultMessage(string baseMessage, OVRSpatialAnchor.OperationResult status)
    {
        switch (status)
        {
            case OVRSpatialAnchor.OperationResult.Success:
                return baseMessage;

            case OVRSpatialAnchor.OperationResult.Failure_SpaceCloudStorageDisabled:
                return baseMessage + "\n空間アンカーのクラウド保存／共有が無効になっています。\n" +
                       "ヘッドセットの設定で『拡張空間サービス（Enhanced Spatial Services）』を有効にしてください。";

            case OVRSpatialAnchor.OperationResult.Failure_SpaceMappingInsufficient:
                return baseMessage + "\n現在の部屋のマッピング情報が不十分です。\n" +
                       "アンカーがあるエリアを中心に、周囲をゆっくり見回してから再試行してください。";

            case OVRSpatialAnchor.OperationResult.Failure_SpaceLocalizationFailed:
                return baseMessage + "\nホストがアンカーを保存した場所と十分に一致していません。\n" +
                       "ホストと同じ位置・向きに近づき、周囲を見回してから再試行してください。";

            case OVRSpatialAnchor.OperationResult.Failure_SpaceNetworkTimeout:
                return baseMessage + "\nネットワークのタイムアウトが発生しました。\n" +
                       "Wi-Fi 接続を確認し、少し待ってから再試行してください。";

            case OVRSpatialAnchor.OperationResult.Failure_SpaceNetworkRequestFailed:
                return baseMessage + "\nネットワーク接続に問題が発生しました。\n" +
                       "インターネット接続を確認し、安定した環境で再試行してください。";

            default:
                return $"{baseMessage}\nエラーコード: {status}";
        }
    }
}
