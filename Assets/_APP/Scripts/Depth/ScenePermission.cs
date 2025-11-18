#if UNITY_ANDROID
using UnityEngine.Android;
#endif
using UnityEngine;

public class ScenePermission : MonoBehaviour
{
#if UNITY_ANDROID
  const string ID = "com.oculus.permission.USE_SCENE";
  void Start()
  {
    if (!Permission.HasUserAuthorizedPermission(ID))
    {
      var cb = new PermissionCallbacks();
      Permission.RequestUserPermission(ID, cb);
    }
  }
#endif
}
