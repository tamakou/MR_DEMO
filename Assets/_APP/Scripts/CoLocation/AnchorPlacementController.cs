using System;
using UnityEngine;

/// <summary>
/// コントローラのレイキャストで Shared Anchor の設置位置を決める。
/// </summary>
public class AnchorPlacementController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform controllerTransform;
    [SerializeField] private GameObject previewPrefab;
    [SerializeField] private LineRenderer laserLineRenderer;

    [Header("Settings")]
    [SerializeField] private LayerMask targetLayer = ~0; // 全レイヤー
    [SerializeField] private float maxDistance = 10f;

    public Action<Vector3, Quaternion> OnConfirmed;

    private GameObject _previewInstance;
    private bool _isActive = false;

    private void Awake()
    {
        // コントローラ参照が無ければ RightHandAnchor → Camera.main の順に探す
        if (controllerTransform == null)
        {
            var rightHand = GameObject.Find("RightHandAnchor");
            if (rightHand != null) controllerTransform = rightHand.transform;
            else if (Camera.main != null) controllerTransform = Camera.main.transform;
        }
    }

    private void Start()
    {
        if (previewPrefab != null)
        {
            _previewInstance = Instantiate(previewPrefab);
        }
        else
        {
            // 簡易ゴーストキューブ
            _previewInstance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _previewInstance.transform.localScale = Vector3.one * 0.1f;
            var renderer = _previewInstance.GetComponent<Renderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Standard"));
                mat.color = new Color(0f, 1f, 0f, 0.35f);
                renderer.material = mat;
            }
            Destroy(_previewInstance.GetComponent<Collider>()); // 自分自身に Ray が当たらないように
        }

        if (_previewInstance != null) _previewInstance.SetActive(false);

        if (laserLineRenderer != null)
        {
            laserLineRenderer.positionCount = 2;
            laserLineRenderer.enabled = false;
        }
    }

    public void BeginPlacement()
    {
        _isActive = true;
        if (_previewInstance != null) _previewInstance.SetActive(true);
        if (laserLineRenderer != null) laserLineRenderer.enabled = true;
        Debug.Log("[AnchorPlacementController] Placement Mode Started.");
    }

    public void EndPlacement()
    {
        _isActive = false;
        if (_previewInstance != null) _previewInstance.SetActive(false);
        if (laserLineRenderer != null) laserLineRenderer.enabled = false;
        Debug.Log("[AnchorPlacementController] Placement Mode Ended.");
    }

    private void Update()
    {
        if (!_isActive || controllerTransform == null) return;

        Ray ray = new Ray(controllerTransform.position, controllerTransform.forward);
        Vector3 targetPoint;
        Quaternion targetRotation;

        if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, targetLayer))
        {
            targetPoint = hit.point;

            // 衝突した面の法線に対して「上方向」を合わせる。
            // 床なら Identity、壁なら壁に垂直に立つイメージになる。
            targetRotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
        }
        else
        {
            targetPoint = ray.GetPoint(maxDistance);
            targetRotation = Quaternion.LookRotation(controllerTransform.forward, Vector3.up);
        }

        if (_previewInstance != null)
        {
            _previewInstance.transform.SetPositionAndRotation(targetPoint, targetRotation);
        }

        if (laserLineRenderer != null)
        {
            laserLineRenderer.SetPosition(0, controllerTransform.position);
            laserLineRenderer.SetPosition(1, targetPoint);
        }

        // トリガーで決定
        if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) ||
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
        {
            Debug.Log($"[AnchorPlacementController] Placement Confirmed at {targetPoint}");
            OnConfirmed?.Invoke(targetPoint, targetRotation);
            EndPlacement();
        }
    }
}
