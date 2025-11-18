using UnityEngine;

public sealed class DepthPermissionRequester : MonoBehaviour
{
  const string SpatialPermission = "com.oculus.permission.USE_SCENE";

  void Start()
  {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(SpatialPermission))
        {
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => Debug.Log("USE_SCENE Granted");
            callbacks.PermissionDenied  += _ => Debug.LogWarning("USE_SCENE Denied");
            UnityEngine.Android.Permission.RequestUserPermission(SpatialPermission, callbacks);
        }
#endif
  }
}
