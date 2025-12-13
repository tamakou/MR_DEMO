// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.XR.MRUtilityKit;
using Meta.XR.Samples;
using UnityEngine;

namespace MRUtilityKitSample.NavMesh
{
    [MetaCodeSample("MRUK-NavMesh")]
    public class NavMeshSampleController : MonoBehaviour
    {
        public SceneNavigation SceneNavigation;
        public bool useGlobalMesh = false;

        // Update is called once per frame
        void Update()
        {
            if (OVRInput.GetDown(OVRInput.RawButton.B))
            {
                if (!SceneNavigation)
                {
                    return;
                }

                useGlobalMesh = !useGlobalMesh;
                SceneNavigation.ToggleGlobalMeshNavigation(useGlobalMesh);
            }
        }
    }
}
