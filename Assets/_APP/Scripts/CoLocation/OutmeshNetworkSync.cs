using Fusion;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Synchronizes the locally loaded OutmeshRoot object across the network using a "Tracker" pattern.
/// This script should be attached to a separate NetworkObject (the "Tracker") spawned by Fusion.
/// It finds the local "OutmeshRoot" and syncs its position/rotation.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]
public class OutmeshNetworkSync : NetworkBehaviour
{
    private Transform _localOutmeshRoot;
    private UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable _grabInteractable;
    private bool _subscribed = false;

    public override void Spawned()
    {
        TryInitializeOutmeshRoot();
    }

    private void TryInitializeOutmeshRoot()
    {
        if (_localOutmeshRoot != null) return; // Already initialized

        // Find the local OutmeshRoot object
        // We assume OutmeshRuntimeLoader creates it with this name.
        GameObject rootObj = GameObject.Find("OutmeshRoot");
        if (rootObj != null)
        {
            _localOutmeshRoot = rootObj.transform;
            Debug.Log("[OutmeshNetworkSync] Found local OutmeshRoot.");

            // If we are the State Authority (e.g. Host initially), snap the Tracker to the Mesh
            // Object might be null if called before Spawned(), so check first
            if (Object != null && Object.HasStateAuthority)
            {
                transform.position = _localOutmeshRoot.position;
                transform.rotation = _localOutmeshRoot.rotation;
            }
            // If we are a Client (or Object not ready), snap the Mesh to the Tracker (which has the synced pos)
            else if (Object != null)
            {
                _localOutmeshRoot.position = transform.position;
                _localOutmeshRoot.rotation = transform.rotation;
            }

            FindAndSubscribeToGrab();
        }
        else
        {
            Debug.LogWarning("[OutmeshNetworkSync] OutmeshRoot not found in scene!");
        }
    }

    public override void FixedUpdateNetwork()
    {
        if (_localOutmeshRoot == null) return;

        if (Object.HasStateAuthority)
        {
            // We own the object. Update the NetworkTransform (this object) to match the Local Mesh.
            // This broadcasts the position to others.
            transform.position = _localOutmeshRoot.position;
            transform.rotation = _localOutmeshRoot.rotation;
        }
        else
        {
            // We are a proxy.
            // Check if we are currently grabbing it locally. If so, DO NOT overwrite with network data.
            // This prevents "fighting" while waiting for authority transfer.
            bool isLocallyGrabbed = _grabInteractable != null && _grabInteractable.isSelected;

            if (!isLocallyGrabbed)
            {
                // Update the Local Mesh to match the NetworkTransform (this object).
                _localOutmeshRoot.position = transform.position;
                _localOutmeshRoot.rotation = transform.rotation;
            }
        }
    }

    private void Update()
    {
        // Retry finding root if missing (OutmeshRoot may be loaded after this object spawns)
        if (_localOutmeshRoot == null)
        {
            TryInitializeOutmeshRoot();
        }

        if (_localOutmeshRoot != null && !_subscribed)
        {
            FindAndSubscribeToGrab();
        }
    }

    private void FindAndSubscribeToGrab()
    {
        if (_localOutmeshRoot == null) return;

        if (_grabInteractable == null)
        {
            // The interactable is likely on the OutmeshRoot or a child
            _grabInteractable = _localOutmeshRoot.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        }

        if (_grabInteractable != null && !_subscribed)
        {
            _grabInteractable.selectEntered.AddListener(OnGrabbed);
            _subscribed = true;
            Debug.Log("[OutmeshNetworkSync] Subscribed to XRGrabInteractable events on local object.");
        }
    }

    private void OnDestroy()
    {
        if (_grabInteractable != null)
        {
            _grabInteractable.selectEntered.RemoveListener(OnGrabbed);
        }
    }

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        // When grabbed by a local interactor, request authority
        if (!Object.HasStateAuthority)
        {
            Debug.Log("[OutmeshNetworkSync] Object grabbed. Requesting State Authority...");
            Object.RequestStateAuthority();
        }
    }
}
