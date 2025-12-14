using System;
using UnityEngine;
using Unity.XR.CoreUtils;

public class SpaceAlignmentManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform cameraRig;
    [SerializeField] private SharedAnchorManager sharedAnchorManager;

    [Header("Alignment Settings")]
    [Tooltip("アンカーを基準にしたいワールド上のターゲット位置。\n例：Vector3.zero にするとアンカー位置がワールド原点に来るように合わせます。")]
    [SerializeField] private Vector3 worldOffset = Vector3.zero;

    [Tooltip("true: アンカー回転はYawのみ採用（推奨：床設置で安定）")]
    [SerializeField] private bool useYawOnly = true;

    [Tooltip("true: 同じアンカーUUIDに対しては1回だけアライン（繰り返し補正を避ける）")]
    [SerializeField] private bool alignOnlyOnce = true;

    private Guid? _alignedAnchorUuid;

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
    /// ★修正版：Delta(差分)をRigへ適用する方式。
    /// </summary>
    private void AlignToAnchor(OVRSpatialAnchor anchor)
    {
        if (cameraRig == null)
        {
            Debug.LogError("[SpaceAlignmentManager] CameraRig not found! Cannot align.");
            return;
        }
        if (anchor == null) return;

        if (alignOnlyOnce && _alignedAnchorUuid.HasValue && _alignedAnchorUuid.Value == anchor.Uuid)
        {
            return;
        }

        var aT = anchor.transform;

        Vector3 anchorPos = aT.position;
        Quaternion anchorRot = useYawOnly
            ? Quaternion.Euler(0f, aT.eulerAngles.y, 0f)
            : aT.rotation;

        // 目標：アンカーが worldOffset の位置、回転は(0,0,0)（Yaw=0）に来るようにする
        Quaternion targetRot = Quaternion.identity;
        Vector3 targetPos = worldOffset;

        // Delta = target * inverse(current)
        Quaternion deltaRot = targetRot * Quaternion.Inverse(anchorRot);
        Vector3 deltaPos = targetPos - (deltaRot * anchorPos);

        // Rig に Delta を適用
        cameraRig.rotation = deltaRot * cameraRig.rotation;
        cameraRig.position = deltaRot * cameraRig.position + deltaPos;

        _alignedAnchorUuid = anchor.Uuid;

        Debug.Log($"[SpaceAlignmentManager] Aligned to anchor {anchor.Uuid}. Rig Pos={cameraRig.position}, Rot={cameraRig.rotation.eulerAngles}");
    }
}
