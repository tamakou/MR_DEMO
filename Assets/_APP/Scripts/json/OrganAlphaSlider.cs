using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public sealed class OrganAlphaSlider : MonoBehaviour
{
  [Header("対象 outmesh / 臓器")]
  public Transform outmeshRoot;
  [Tooltip("例: heart / lung / liver など（body 以外に設定可能）。部分一致・大文字小文字/記号無視")]
  public string organKey = "heart";

  [Header("UI")]
  public Slider slider;       // Min=0, Max=255, WholeNumbers=ON
  public Text valueText;      // "heart α : 128/255" の表記

  [Header("Preset 連携")]
  public PresetManager presetManager;  // 省略時は自動検索

  // --- URP プロパティ
  static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
  static readonly int ID_Surface = Shader.PropertyToID("_Surface");
  static readonly int ID_Blend = Shader.PropertyToID("_Blend");
  static readonly int ID_SrcBlend = Shader.PropertyToID("_SrcBlend");
  static readonly int ID_DstBlend = Shader.PropertyToID("_DstBlend");
  static readonly int ID_AlphaClip = Shader.PropertyToID("_AlphaClip");
  static readonly int ID_ZWriteCtl = Shader.PropertyToID("_ZWriteControl");
  static readonly int ID_Cull = Shader.PropertyToID("_Cull");
  const string KW_SURFACE_TRANSPARENT = "_SURFACE_TYPE_TRANSPARENT";

  readonly List<(Renderer r, int subCount)> _targets = new();
  readonly Dictionary<Renderer, Material[]> _originalShared = new();
  readonly Dictionary<Material, Material> _transparentCache = new();
  MaterialPropertyBlock _mpb;

  void Awake()
  {
    if (outmeshRoot == null) { Debug.LogError("[OrganAlphaSlider] outmeshRoot 未設定"); enabled = false; return; }
    if (slider == null) { Debug.LogError("[OrganAlphaSlider] slider 未設定"); enabled = false; return; }

    if (presetManager == null) presetManager = FindFirstObjectByType<PresetManager>();
    _mpb = new MaterialPropertyBlock();
    CacheTargets();

    slider.onValueChanged.AddListener(OnSliderChanged);

    // 起動直後：プリセットが既に適用済みならスライダーに反映
    if (presetManager != null && presetManager.TryGetAppliedAlpha(organKey, out var a))
      slider.SetValueWithoutNotify(a);
    UpdateLabel();

    // プリセット適用時の同期（UIだけ更新。モデルへの適用はPresetManager側が実施済み）
    if (presetManager != null)
      presetManager.OnAlphaApplied += HandlePresetAlphaApplied;
  }

  void OnDestroy()
  {
    if (slider != null) slider.onValueChanged.RemoveListener(OnSliderChanged);
    if (presetManager != null) presetManager.OnAlphaApplied -= HandlePresetAlphaApplied;
  }

  // ---- スライダー操作（0..255）
  void OnSliderChanged(float v)
  {
    ApplyAlpha((int)v);   // モデルへ即時反映
    UpdateLabel();
  }

  void UpdateLabel()
  {
    if (valueText != null) valueText.text = $"{organKey} α : {(int)slider.value}/255";
  }

  void HandlePresetAlphaApplied(string keyNormalized, int a255)
  {
    if (Normalize(organKey) != keyNormalized) return;
    // UIだけ同期（PresetManagerがモデルへ適用済み）
    slider.SetValueWithoutNotify(a255);
    UpdateLabel();
  }

  // ---- 実体：α を反映（必要時 Transparent 化）
  void ApplyAlpha(int a255)
  {
    float af = Mathf.Clamp01(a255 / 255f);
    bool needsTransparent = a255 < 255;

    foreach (var (r, subCount) in _targets)
    {
      if (needsTransparent) AssignTransparent(r);
      else RestoreOriginal(r);

      // 片面維持（負荷抑制）
      ForceCullBack(r);

      for (int i = 0; i < subCount; i++)
      {
        _mpb.Clear();
        r.GetPropertyBlock(_mpb, i);
        Color col = _mpb.HasColor(ID_BaseColor)
                    ? _mpb.GetColor(ID_BaseColor)
                    : (r.sharedMaterials != null && i < r.sharedMaterials.Length &&
                       r.sharedMaterials[i] != null && r.sharedMaterials[i].HasProperty(ID_BaseColor))
                      ? r.sharedMaterials[i].GetColor(ID_BaseColor)
                      : Color.white;
        col.a = af;
        _mpb.Clear();
        _mpb.SetColor(ID_BaseColor, col);
        r.SetPropertyBlock(_mpb, i);
      }
    }
  }

  // ---- 検索／切替ユーティリティ -----------------------------
  void CacheTargets()
  {
    _targets.Clear();
    string key = Normalize(organKey);
    var all = outmeshRoot.GetComponentsInChildren<Renderer>(true);
    foreach (var r in all)
    {
      Transform t = r.transform;
      while (t != null && t != outmeshRoot.parent)
      {
        if (Normalize(t.name).Contains(key))
        {
          _targets.Add((r, r.sharedMaterials?.Length ?? 1));
          break;
        }
        t = t.parent;
      }
    }
    if (_targets.Count == 0)
      Debug.LogWarning($"[OrganAlphaSlider] '{organKey}' に一致する Renderer が見つかりません。");
  }

  void RestoreOriginal(Renderer r)
  {
    if (_originalShared.TryGetValue(r, out var orig))
      r.sharedMaterials = orig;
  }

  void AssignTransparent(Renderer r)
  {
    var mats = r.sharedMaterials; if (mats == null) return;
    bool changed = false;
    for (int i = 0; i < mats.Length; i++)
    {
      var src = mats[i]; if (src == null) continue;
      if (IsTransparent(src)) continue;

      if (!_originalShared.ContainsKey(r))
        _originalShared[r] = r.sharedMaterials;

      if (!_transparentCache.TryGetValue(src, out var v))
      {
        v = CreateTransparentVariant(src);
        _transparentCache[src] = v;
      }
      mats[i] = v; changed = true;
    }
    if (changed) r.sharedMaterials = mats;
  }

  static bool IsTransparent(Material m)
  {
    bool surf = m.HasProperty(ID_Surface) && m.GetFloat(ID_Surface) > 0.5f;
    bool kw = m.IsKeywordEnabled(KW_SURFACE_TRANSPARENT);
    string tag = m.GetTag("RenderType", false);
    return surf || kw || string.Equals(tag, "Transparent", System.StringComparison.OrdinalIgnoreCase);
  }

  static Material CreateTransparentVariant(Material src)
  {
    var m = new Material(src) { name = src.name + " (TransparentVariant)" };

    if (m.HasProperty(ID_Surface)) m.SetFloat(ID_Surface, 1f); // Transparent
    if (m.HasProperty(ID_Blend)) m.SetFloat(ID_Blend, 0f); // Alpha
    if (m.HasProperty(ID_AlphaClip)) m.SetFloat(ID_AlphaClip, 0f);
    if (m.HasProperty(ID_SrcBlend)) m.SetInt(ID_SrcBlend, (int)BlendMode.SrcAlpha);
    if (m.HasProperty(ID_DstBlend)) m.SetInt(ID_DstBlend, (int)BlendMode.OneMinusSrcAlpha);
    if (m.HasProperty(ID_ZWriteCtl)) m.SetInt(ID_ZWriteCtl, 0);
    if (m.HasProperty(ID_Cull)) m.SetInt(ID_Cull, (int)CullMode.Back);

    m.EnableKeyword(KW_SURFACE_TRANSPARENT);
    m.SetOverrideTag("RenderType", "Transparent");
    m.renderQueue = (int)RenderQueue.Transparent;
    return m;
  }

  static void ForceCullBack(Renderer r)
  {
    var mats = r.sharedMaterials; if (mats == null) return;
    bool changed = false;
    for (int i = 0; i < mats.Length; i++)
    {
      var m = mats[i];
      if (m != null && m.HasProperty(ID_Cull))
      {
        int cur = (int)m.GetFloat(ID_Cull);
        if (cur != (int)CullMode.Back) { m.SetInt(ID_Cull, (int)CullMode.Back); changed = true; }
      }
    }
    if (changed) r.sharedMaterials = mats;
  }

  static string Normalize(string s)
  {
    if (string.IsNullOrEmpty(s)) return "";
    s = s.ToLowerInvariant();
    return s.Replace("_", "").Replace("-", "").Replace(" ", "");
  }
  /// <summary>
  /// GLB をランタイムロードした後に、対象 Renderer を再スキャンするためのフック。
  /// </summary>
  public void RefreshTargets()
  {
    CacheTargets();
  }
}

