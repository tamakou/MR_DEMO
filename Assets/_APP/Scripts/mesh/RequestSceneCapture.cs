using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR.Features.Meta;

public class RequestSceneCapture : MonoBehaviour
{
  [ContextMenu("Request Scene Capture")]
  public void DoCapture()
  {
    var arSession = FindAnyObjectByType<ARSession>();
    var ok = (arSession.subsystem as MetaOpenXRSessionSubsystem)?.TryRequestSceneCapture() ?? false;
    Debug.Log("SceneCapture requested: " + ok);
  }
}
