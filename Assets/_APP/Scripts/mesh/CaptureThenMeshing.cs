using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR.Features.Meta;

public class CaptureThenMeshing : MonoBehaviour
{
  [SerializeField] ARMeshManager meshManager;
  bool pendingRefresh;

  void Awake()
  {
    if (meshManager) meshManager.enabled = false; // 起動時はメッシュ生成しない
  }

  // 「再スキャン→メッシュ開始」ボタンの OnClick に割り当て
  public void OnPressScan()
  {
    // 1) その場で Scene Capture を起動（OS のスキャン UI を表示）
    var ar = FindAnyObjectByType<ARSession>();
    (ar.subsystem as MetaOpenXRSessionSubsystem)?.TryRequestSceneCapture(); // ここで一時停止
    pendingRefresh = true; // 復帰後にメッシュ生成
  }

  // 公式ライフサイクルどおり：スキャンUIへ遷移→戻りで OnApplicationPause(false)
  void OnApplicationPause(bool paused)
  {
    if (!paused && pendingRefresh && meshManager)
    {
      // 2) 復帰直後：古いメッシュを消し、最新の Scene Model で生成開始
      meshManager.enabled = false;
      meshManager.DestroyAllMeshes();  // 公式API：既存メッシュ破棄＆保留中も無視
      meshManager.enabled = true;      // ここからメッシュ表示＋MeshCollider が自動作成
      pendingRefresh = false;
    }
  }

  // 任意：メッシュを隠すボタン（生成停止＋破棄）
  public void HideMesh()
  {
    if (!meshManager) return;
    meshManager.enabled = false;
    meshManager.DestroyAllMeshes();
  }
}
