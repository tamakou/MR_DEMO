using UnityEngine;
using Unity.XR.CoreUtils;

public class SpaceAlignmentManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraRig;
    [SerializeField] private SharedAnchorManager sharedAnchorManager;

    [Header("Alignment Settings")]
    [Tooltip("アンカー位置を基準(0,0,0)としたときの、ワールド全体のオフセット。\n例えば Y=0.05 にすると、コンテンツ(0,0,0)がアンカーより 5cm 高い位置に表示されます（CameraRigを-0.05下げることで実現）。")]
    [SerializeField] private Vector3 worldOffset = Vector3.zero;

    private void Awake()
    {
        if (sharedAnchorManager == null)
            sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();

        if (cameraRig == null)
        {
            // OVRCameraRig → XROrigin の順で探す
            var ovrRig = GameObject.Find("OVRCameraRig");
            if (ovrRig != null)
            {
                cameraRig = ovrRig.transform;
            }
            else
            {
                var xrOrigin = FindFirstObjectByType<XROrigin>();
                if (xrOrigin != null) cameraRig = xrOrigin.transform;
            }
        }
    }

    private void OnEnable()
    {
        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.OnAnchorLocalized -= AlignToAnchor;
            sharedAnchorManager.OnAnchorLocalized += AlignToAnchor;
        }
    }

    private void OnDisable()
    {
        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.OnAnchorLocalized -= AlignToAnchor;
        }
    }

    /// <summary>
    /// ローカライズされたアンカーを基準に CameraRig を移動・回転する。
    /// アンカー位置 ＝ ワールド(0,0,0) ＋ worldOffset となるように調整。
    /// </summary>
    private void AlignToAnchor(OVRSpatialAnchor anchor)
    {
        if (cameraRig == null)
        {
            Debug.LogError("[SpaceAlignmentManager] CameraRig not found! Cannot align.");
            return;
        }

        var anchorTransform = anchor.transform;

        Debug.Log(
            $"[SpaceAlignmentManager] Aligning CameraRig to Anchor: {anchor.Uuid}, " +
            $"Rig Pos={cameraRig.position}, Anchor Pos={anchorTransform.position}");

        // 1. まずアンカーを原点 (0,0,0) に合わせるためのリグ位置を計算
        //    (アンカー空間における (0,0,0) の座標 = リグを置くべき位置)
        //    InverseTransformPoint(Vector3.zero) は、アンカーから見た (0,0,0) の相対位置。
        //    これをリグ位置にセットすると、(0,0,0) がアンカー位置に重なる。
        Vector3 targetRigPos = anchorTransform.InverseTransformPoint(Vector3.zero);

        // 2. 回転のアラインメント（Y軸回転のみ合わせる）
        var anchorEuler = anchorTransform.eulerAngles;
        // リグを -Y 回転させることで、ワールドの正面をアンカーの向きに合わせる
        Vector3 targetRigRot = new Vector3(0f, -anchorEuler.y, 0f);

        // 3. オフセットの適用
        //    ワールドを「上にずらしたい (Y+)」なら、リグ（カメラ）を「下にずらす (Y-)」必要がある。
        //    オフセットはワールド座標系での移動量とみなして引く。
        //    ただし、Rig の回転も考慮する必要があるため、単純な引き算ではなく Local 空間での調整か、
        //    あるいはリグ設定後にリグローカルで動かすのが安全。

        // シンプルに「アンカー位置での補正」として計算する。
        // 「物体を (0.05, 0.05, 0.05) にずらしたい」＝「(0,0,0) にある物体が (0.05...) に見える」
        // ＝ ワールド全体を (0.05...) ずらす
        // ＝ リグを (-0.05...) ずらす。

        // ここでの worldOffset は「ワールド座標系（回転適用後）」でのシフト量とする。
        targetRigPos -= worldOffset;

        // 適用
        cameraRig.position = targetRigPos;
        cameraRig.eulerAngles = targetRigRot;

        Debug.Log($"[SpaceAlignmentManager] Aligned! New Rig Pos={cameraRig.position}, Rot={cameraRig.rotation.eulerAngles}. Offset applied: {-worldOffset}");
    }
}
