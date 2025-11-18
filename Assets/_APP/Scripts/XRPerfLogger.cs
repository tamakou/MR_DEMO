using UnityEngine;
using UnityEngine.XR;

public class XRPerfLogger : MonoBehaviour
{
  void Update()
  {
    // GPU時間は「秒」なので表示をmsに変換
    if (XRStats.TryGetGPUTimeLastFrame(out float gpuSec))
      Debug.Log($"GPU: {gpuSec * 1000f:F2} ms");

    // ドロップは「前回取得以降の数」
    if (XRStats.TryGetDroppedFrameCount(out int dropped))
      if (dropped > 0) Debug.Log($"Dropped frames (since last): {dropped}");
  }
}
