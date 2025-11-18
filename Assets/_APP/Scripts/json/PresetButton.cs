// Assets/Scripts/PresetButton.cs
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public sealed class PresetButton : MonoBehaviour
{
  [Tooltip("Resources 内の JSON 名（拡張子なし）。例: preset7")]
  public string resourceName = "preset7";

  [Tooltip("シーン内の PresetManager 参照（未指定なら自動検索）")]
  public PresetManager manager;

  void Awake()
  {
    if (manager == null) manager = FindFirstObjectByType<PresetManager>();
    var btn = GetComponent<Button>();
    btn.onClick.AddListener(OnClick);
  }

  void OnClick()
  {
    if (manager == null) { Debug.LogError("[PresetButton] PresetManager が見つかりません"); return; }
    if (string.IsNullOrEmpty(resourceName)) { Debug.LogError("[PresetButton] resourceName が未設定"); return; }
    manager.ApplyPresetResource(resourceName);
  }
}
