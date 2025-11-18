using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

/// <summary>
/// スライダーで「body以外の臓器すべて」の透過度(0..255)を一括変更する軽量実装。
/// ・_BaseColor の α を MaterialPropertyBlock で上書き（RGBは保持）
/// ・α<255 のときだけ URP Transparent バリアントに差し替え、α=255 で元に戻す
/// ・除外名（例: "body"）は配列で指定。部分一致・大文字小文字/_/-/空白を無視。
/// ・PresetManager があれば、プリセット適用時にグループの平均αをスライダーに同期。
/// </summary>
[DisallowMultipleComponent]
public sealed class GroupAlphaSlider : MonoBehaviour
{
  [Header("対象 outmesh のルート")]
  public Transform outmeshRoot;

  [Header("除外キー（例: body）。部分一致・小文字化して比較")]
  public string[] excludeKeys = new[] { "body" };

  [Header("UI")]
  public Slider slider;           // Min=0, Max=255, WholeNumbers=ON を推奨
  public Text valueText;          // Others α : 128/255 のように表示

  [Header("Preset 連携（任意）")]
  public PresetManager presetManager;

  // ---- URP プロパティ
  static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
  static readonly int ID_Surface = Shader.PropertyToID("_Surface");
  static readonly int ID_Blend = Shader.PropertyToID("_Blend");
  static readonly int ID_SrcBlend = Shader.PropertyToID("_SrcBlend");
  static readonly int ID_DstBlend = Shader.PropertyToID("_DstBlend");
  static readonly int ID_AlphaClip = Shader.PropertyToID("_AlphaClip");
  static readonly int ID_ZWriteCtl = Shader.PropertyToID("_ZWriteControl");
  static readonly int ID_Cull = Shader.PropertyToID("_Cull");
  const string KW_SURFACE_TRANSPARENT = "_SURFACE_TYPE_TRANSPARENT";

  // 対象Rendererとサブメッシュ数
  readonly List<(Renderer r, int subCount)> _targets = new();

  // 透明バリアント／元マテリアル
  readonly Dictionary<Renderer, Material[]> _originalShared = new();
  readonly Dictionary<Material, Material> _transparentCache = new();

  // プリセット適用値の一時保持（UI同期用）
  readonly Dictionary<string, int> _lastPresetAByKey = new();

  MaterialPropertyBlock _mpb;

  void Awake()
  {
    if (outmeshRoot == null) { Debug.LogError("[GroupAlphaSlider] outmeshRoot 未設定"); enabled = false; return; }
    if (slider == null) { Debug.LogError("[GroupAlphaSlider] slider 未設定"); enabled = false; return; }

    _mpb = new MaterialPropertyBlock();
    CacheTargets();

    // UI
    slider.minValue = 0f; slider.maxValue = 255f; slider.wholeNumbers = true;
    slider.onValueChanged.AddListener(OnSliderChanged);
    UpdateLabel((int)slider.value);

    // プリセット連携（任意）
    if (presetManager == null) presetManager = FindFirstObjectByType<PresetManager>();
    if (presetManager != null)
      presetManager.OnAlphaApplied += HandlePresetAlphaApplied;
  }

  void OnDestroy()
  {
    if (slider != null) slider.onValueChanged.RemoveListener(OnSliderChanged);
    if (presetManager != null) presetManager.OnAlphaApplied -= HandlePresetAlphaApplied;
  }

  // ---------- スライダー操作：0..255 を一括適用 ----------
  void OnSliderChanged(float v)
  {
    int a255 = Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);
    ApplyUniformAlpha(a255);
    UpdateLabel(a255);
  }

  void ApplyUniformAlpha(int a255)
  {
    float af = Mathf.Clamp01(a255 / 255f);
    bool needsTransparent = a255 < 255;

    foreach (var (r, subCount) in _targets)
    {
      if (!_originalShared.ContainsKey(r))
        _originalShared[r] = r.sharedMaterials;

      // αに応じて Transparent 切替／復帰
      if (needsTransparent) AssignTransparent(r);
      else RestoreOriginal(r);

      // 片面カリングを維持（負荷低減）
      ForceCullBack(r);

      // MPBでαのみ上書き（RGBは維持）
      for (int i = 0; i < subCount; i++)
      {
        _mpb.Clear();
        r.GetPropertyBlock(_mpb, i);
        Color col = (_mpb.GetVector(ID_BaseColor) != Vector4.zero)
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

  // ---------- プリセット適用時：UI を平均値で同期（モデル側はPresetManagerが反映済み） ----------
  void HandlePresetAlphaApplied(string keyNormalized, int a255)
  {
    if (IsExcludedKey(keyNormalized)) return; // body 等は無視
    _lastPresetAByKey[keyNormalized] = Mathf.Clamp(a255, 0, 255);

    // グループの平均値を算出してスライダーに表示だけ同期（上書きはしない）
    int avg = AveragePresetA();
    slider.SetValueWithoutNotify(avg);
    UpdateLabel(avg);
  }

  int AveragePresetA()
  {
    if (_lastPresetAByKey.Count == 0) return (int)slider.value;
    long sum = 0;
    foreach (var v in _lastPresetAByKey.Values) sum += v;
    return Mathf.Clamp(Mathf.RoundToInt(sum / (float)_lastPresetAByKey.Count), 0, 255);
  }

  // ---------- 対象抽出・補助 ----------
  void CacheTargets()
  {
    _targets.Clear();
    var all = outmeshRoot.GetComponentsInChildren<Renderer>(true);
    foreach (var r in all)
    {
      if (IsExcludedTransform(r.transform)) continue;
      _targets.Add((r, r.sharedMaterials?.Length ?? 1));
    }
    if (_targets.Count == 0)
      Debug.LogWarning("[GroupAlphaSlider] 対象Rendererが見つかりません（除外条件が厳しすぎる可能性）。");
  }

  bool IsExcludedTransform(Transform t)
  {
    while (t != null && t != outmeshRoot.parent)
    {
      string n = Normalize(t.name);
      if (IsExcludedKey(n)) return true;
      t = t.parent;
    }
    return false;
  }

  bool IsExcludedKey(string normalized)  // "body" など
  {
    if (excludeKeys == null) return false;
    foreach (var k in excludeKeys)
    {
      if (string.IsNullOrEmpty(k)) continue;
      if (normalized.Contains(Normalize(k))) return true;
    }
    return false;
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
    if (m.HasProperty(ID_ZWriteCtl)) m.SetInt(ID_ZWriteCtl, 0);  // Auto
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

  void UpdateLabel(int a255)
  {
    if (valueText != null) valueText.text = $"Others α : {a255}/255";
  }

  /// <summary>
  /// GLB をランタイムロードした後に、対象 Renderer を再スキャンするためのフック。
  /// </summary>
  public void RefreshTargets()
  {
    CacheTargets();
  }
}

