using Fusion;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Synchronizes the visual state (Presets and Alpha Sliders) of the Outmesh object.
/// Requires NetworkObject.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class OutmeshVisualSync : NetworkBehaviour
{
    [Networked]
    public NetworkString<_32> ActivePreset { get; set; }

    // Version counter to trigger updates for the dictionary
    [Networked]
    public int AlphaVersion { get; set; }

    [Networked]
    [Capacity(32)] // Adjust capacity as needed
    private NetworkDictionary<NetworkString<_32>, int> OrganAlphas { get; }

    private ChangeDetector _changeDetector;
    private PresetManager _presetManager;
    private OrganAlphaSlider[] _organSliders;
    private GroupAlphaSlider[] _groupSliders;
    private bool _isApplyingNetworkUpdate = false;

    public override void Spawned()
    {
        _changeDetector = GetChangeDetector(ChangeDetector.Source.SimulationState);
        _presetManager = GetComponentInChildren<PresetManager>();
        if (_presetManager == null) _presetManager = FindFirstObjectByType<PresetManager>();

        // Find all OrganAlphaSliders in the scene (or children)
        _organSliders = FindObjectsByType<OrganAlphaSlider>(FindObjectsSortMode.None);
        _groupSliders = FindObjectsByType<GroupAlphaSlider>(FindObjectsSortMode.None);

        if (_presetManager != null)
        {
            _presetManager.OnPresetApplied += OnLocalPresetApplied;
        }

        foreach (var sliderScript in _organSliders)
        {
            var slider = sliderScript.GetComponentInChildren<Slider>();
            if (slider != null)
            {
                slider.onValueChanged.AddListener((val) => OnLocalSliderChanged(sliderScript, val));
            }
        }

        foreach (var groupSlider in _groupSliders)
        {
            var slider = groupSlider.GetComponentInChildren<Slider>();
            if (slider != null)
            {
                // Use a special key prefix for groups to avoid collision with organs
                slider.onValueChanged.AddListener((val) => OnLocalGroupSliderChanged(groupSlider, val));
            }
        }
    }

    private void OnLocalGroupSliderChanged(GroupAlphaSlider sliderScript, float value)
    {
        if (_isApplyingNetworkUpdate) return;

        string key = "GROUP_" + sliderScript.name;
        int alpha = (int)value;

        if (Object.HasStateAuthority)
        {
            if (OrganAlphas.TryGet(key, out int currentAlpha) && currentAlpha == alpha) return;

            OrganAlphas.Set(key, alpha);
            AlphaVersion++;
        }
        else
        {
            Rpc_SetAlpha(key, alpha);
        }
    }



    public override void Render()
    {
        foreach (var change in _changeDetector.DetectChanges(this))
        {
            if (change == nameof(ActivePreset))
            {
                ApplyNetworkPreset();
            }
            if (change == nameof(AlphaVersion))
            {
                ApplyNetworkAlphas();
            }
        }
    }

    private void OnLocalPresetApplied(string presetName)
    {
        if (Object.HasStateAuthority && !_isApplyingNetworkUpdate)
        {
            Debug.Log($"[OutmeshVisualSync] Local Preset Applied: {presetName}. Syncing...");
            ActivePreset = presetName;
        }
        else if (!_isApplyingNetworkUpdate)
        {
            // Request Authority or use RPC to tell Host
            Debug.Log($"[OutmeshVisualSync] Local Preset Applied (Client): {presetName}. Sending RPC...");
            Rpc_SetPreset(presetName);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void Rpc_SetPreset(string presetName)
    {
        Debug.Log($"[OutmeshVisualSync] RPC Received: SetPreset {presetName}");
        ActivePreset = presetName;
    }

    private void OnLocalSliderChanged(OrganAlphaSlider sliderScript, float value)
    {
        if (_isApplyingNetworkUpdate) return;

        string key = GetOrganKey(sliderScript);
        if (string.IsNullOrEmpty(key)) return;

        int alpha = (int)value;

        if (Object.HasStateAuthority)
        {
            if (OrganAlphas.TryGet(key, out int currentAlpha) && currentAlpha == alpha) return;

            OrganAlphas.Set(key, alpha);
            AlphaVersion++; // Trigger update for others
        }
        else
        {
            // Send RPC
            Rpc_SetAlpha(key, alpha);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void Rpc_SetAlpha(string key, int value)
    {
        // Debug.Log($"[OutmeshVisualSync] RPC Received: SetAlpha {key} = {value}"); // Comment out to avoid spam
        if (OrganAlphas.TryGet(key, out int currentAlpha) && currentAlpha == value) return;

        OrganAlphas.Set(key, value);
        AlphaVersion++;
    }

    private void ApplyNetworkPreset()
    {
        if (_presetManager != null && !string.IsNullOrEmpty(ActivePreset.ToString()))
        {
            _isApplyingNetworkUpdate = true;
            Debug.Log($"[OutmeshVisualSync] Applying Network Preset: {ActivePreset}");
            _presetManager.ApplyPresetResource(ActivePreset.ToString());
            _isApplyingNetworkUpdate = false;
        }
    }

    private void ApplyNetworkAlphas()
    {
        _isApplyingNetworkUpdate = true;

        // Sync Organ Sliders
        foreach (var sliderScript in _organSliders)
        {
            string key = GetOrganKey(sliderScript);
            if (!string.IsNullOrEmpty(key) && OrganAlphas.TryGet(key, out int alpha))
            {
                var slider = sliderScript.GetComponentInChildren<Slider>();
                if (slider != null) slider.value = alpha;
            }
        }

        // Sync Group Sliders
        foreach (var groupSlider in _groupSliders)
        {
            string key = "GROUP_" + groupSlider.name;
            if (OrganAlphas.TryGet(key, out int alpha))
            {
                var slider = groupSlider.GetComponentInChildren<Slider>();
                if (slider != null) slider.value = alpha;
            }
        }

        _isApplyingNetworkUpdate = false;
    }

    private string GetOrganKey(OrganAlphaSlider script)
    {
        // Use Reflection to find the key field since we can't easily modify the other script due to encoding issues.
        var type = script.GetType();
        var field = type.GetField("organKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (field == null) field = type.GetField("key", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (field == null) field = type.GetField("Name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        if (field != null)
        {
            return field.GetValue(script) as string;
        }

        Debug.LogWarning($"[OutmeshVisualSync] Could not find 'organKey' field on {script.name}");
        return null;
    }
}
