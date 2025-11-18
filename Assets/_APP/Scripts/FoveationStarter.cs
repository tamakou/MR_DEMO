using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class FoveationStarter : MonoBehaviour
{
  void Start()
  {
    var displays = new List<XRDisplaySubsystem>();
    SubsystemManager.GetSubsystems(displays);
    if (displays.Count > 0)
    {
      displays[0].foveatedRenderingLevel = 0.5f; // ’†’ö“x
    }
  }
}
