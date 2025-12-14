using Fusion;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Synchronizes the visual state (Presets and Alpha Sliders) of the Outmesh object.
/// - Fixes:
///   1) Properly unregisters UI/event listeners on Despawn/Destroy
///   2) Applies current network state on Spawned() for late-join proxies
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class OutmeshVisualSync : NetworkBehaviour
{
    [Networked] public NetworkString<_32> ActivePreset { get; set; }

    // Version counter to trigger updates for the dictionary
    [Networked] public int AlphaVersion { get; set; }

    [Networked]
    [Capacity(32)]
    private NetworkDictionary<NetworkString<_32>, int> OrganAlphas { get; }

    private ChangeDetector _changeDetector;
    private PresetManager _presetManager;
    private OrganAlphaSlider[] _organSliders;
    private GroupAlphaSlider[] _groupSliders;

    private bool _isApplyingNetworkUpdate = false;

    // To remove listeners, we must keep exact delegate instances.
    private bool _listenersRegistered = false;
    private readonly Dictionary<Slider, UnityAction<float>> _sliderActions = new();

    public override void Spawned()
    {
        _changeDetector = GetChangeDetector(ChangeDetector.Source.SimulationState);

        _presetManager = GetComponentInChildren<PresetManager>();
        if (_presetManager == null) _presetManager = FindFirstObjectByType<PresetManager>();

        _organSliders = FindObjectsByType<OrganAlphaSlider>(FindObjectsSortMode.None);
        _groupSliders = FindObjectsByType<GroupAlphaSlider>(FindObjectsSortMode.None);

        RegisterListeners();

        // Late-join / initial apply:
        // Apply current network state explicitly because ChangeDetector can miss initial values on spawn.
        if (!Object.HasStateAuthority)
        {
            ApplyNetworkPreset();
            ApplyNetworkAlphas();
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        UnregisterListeners();
    }

    private void OnDestroy()
    {
        // In case object is destroyed without Despawned (scene unload etc.)
        UnregisterListeners();
    }

    private void RegisterListeners()
    {
        if (_listenersRegistered) return;
        _listenersRegistered = true;

        if (_presetManager != null)
        {
            _presetManager.OnPresetApplied -= OnLocalPresetApplied;
            _presetManager.OnPresetApplied += OnLocalPresetApplied;
        }

        _sliderActions.Clear();

        // Organ sliders
        if (_organSliders != null)
        {
            foreach (var sliderScript in _organSliders)
            {
                if (sliderScript == null) continue;

                var slider = sliderScript.GetComponentInChildren<Slider>();
                if (slider == null) continue;

                UnityAction<float> action = (val) => OnLocalSliderChanged(sliderScript, val);
                _sliderActions[slider] = action;
                slider.onValueChanged.AddListener(action);
            }
        }

        // Group sliders
        if (_groupSliders != null)
        {
            foreach (var groupSlider in _groupSliders)
            {
                if (groupSlider == null) continue;

                var slider = groupSlider.GetComponentInChildren<Slider>();
                if (slider == null) continue;

                UnityAction<float> action = (val) => OnLocalGroupSliderChanged(groupSlider, val);
                _sliderActions[slider] = action;
                slider.onValueChanged.AddListener(action);
            }
        }
    }

    private void UnregisterListeners()
    {
        if (!_listenersRegistered) return;
        _listenersRegistered = false;

        if (_presetManager != null)
        {
            _presetManager.OnPresetApplied -= OnLocalPresetApplied;
        }

        foreach (var kv in _sliderActions)
        {
            var slider = kv.Key;
            var action = kv.Value;

            if (slider != null)
                slider.onValueChanged.RemoveListener(action);
        }

        _sliderActions.Clear();
    }

    public override void Render()
    {
        if (_changeDetector == null) return;

        foreach (var change in _changeDetector.DetectChanges(this))
        {
            if (change == nameof(ActivePreset))
            {
                ApplyNetworkPreset();
            }
            else if (change == nameof(AlphaVersion))
            {
                ApplyNetworkAlphas();
            }
        }
    }

    private void OnLocalGroupSliderChanged(GroupAlphaSlider sliderScript, float value)
    {
        if (_isApplyingNetworkUpdate) return;
        if (sliderScript == null) return;

        string key = "GROUP_" + sliderScript.name;
        int alpha = Mathf.RoundToInt(value);

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

    private void OnLocalPresetApplied(string presetName)
    {
        if (_isApplyingNetworkUpdate) return;
        if (string.IsNullOrEmpty(presetName)) return;

        if (Object.HasStateAuthority)
        {
            if (ActivePreset.ToString() == presetName) return;
            Debug.Log($"[OutmeshVisualSync] Local Preset Applied: {presetName}. Syncing...");
            ActivePreset = presetName;
        }
        else
        {
            Debug.Log($"[OutmeshVisualSync] Local Preset Applied (Client): {presetName}. Sending RPC...");
            Rpc_SetPreset(presetName);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void Rpc_SetPreset(string presetName)
    {
        if (string.IsNullOrEmpty(presetName)) return;
        if (ActivePreset.ToString() == presetName) return;

        Debug.Log($"[OutmeshVisualSync] RPC Received: SetPreset {presetName}");
        ActivePreset = presetName;
    }

    private void OnLocalSliderChanged(OrganAlphaSlider sliderScript, float value)
    {
        if (_isApplyingNetworkUpdate) return;
        if (sliderScript == null) return;

        string key = GetOrganKey(sliderScript);
        if (string.IsNullOrEmpty(key)) return;

        int alpha = Mathf.RoundToInt(value);

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

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void Rpc_SetAlpha(string key, int value)
    {
        if (string.IsNullOrEmpty(key)) return;

        if (OrganAlphas.TryGet(key, out int currentAlpha) && currentAlpha == value) return;

        OrganAlphas.Set(key, value);
        AlphaVersion++;
    }

    private void ApplyNetworkPreset()
    {
        if (_presetManager == null) return;

        string preset = ActivePreset.ToString();
        if (string.IsNullOrEmpty(preset)) return;

        _isApplyingNetworkUpdate = true;
        Debug.Log($"[OutmeshVisualSync] Applying Network Preset: {preset}");
        _presetManager.ApplyPresetResource(preset);
        _isApplyingNetworkUpdate = false;
    }

    private void ApplyNetworkAlphas()
    {
        _isApplyingNetworkUpdate = true;

        // Sync Organ Sliders
        if (_organSliders != null)
        {
            foreach (var sliderScript in _organSliders)
            {
                if (sliderScript == null) continue;

                string key = GetOrganKey(sliderScript);
                if (!string.IsNullOrEmpty(key) && OrganAlphas.TryGet(key, out int alpha))
                {
                    var slider = sliderScript.GetComponentInChildren<Slider>();
                    if (slider != null) slider.value = alpha;
                }
            }
        }

        // Sync Group Sliders
        if (_groupSliders != null)
        {
            foreach (var groupSlider in _groupSliders)
            {
                if (groupSlider == null) continue;

                string key = "GROUP_" + groupSlider.name;
                if (OrganAlphas.TryGet(key, out int alpha))
                {
                    var slider = groupSlider.GetComponentInChildren<Slider>();
                    if (slider != null) slider.value = alpha;
                }
            }
        }

        _isApplyingNetworkUpdate = false;
    }

    private string GetOrganKey(OrganAlphaSlider script)
    {
        // Reflection: fallback chain kept as in original
        var type = script.GetType();

        FieldInfo field =
            type.GetField("organKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            type.GetField("key", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            type.GetField("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (field != null)
        {
            return field.GetValue(script) as string;
        }

        Debug.LogWarning($"[OutmeshVisualSync] Could not find 'organKey' field on {script.name}");
        return null;
    }
}
