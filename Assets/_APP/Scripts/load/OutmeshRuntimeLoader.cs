using System;
using System.IO;
using GLTFast;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

/// <summary>
/// /Android/data/com.Thoracentes.MRZeus/files/models/outmesh.glb を読み込み、
/// OutmeshRoot 配下に生成したあと、
/// - body には M_Body_Transparent
/// - それ以外には meshmate
/// を適用し、XRGrab + XRDualGrabFreeTransformer を設定する。
/// </summary>
[DefaultExecutionOrder(-200)]
public sealed class OutmeshRuntimeLoader : MonoBehaviour
{
  [Header("GLB 読み込み設定")]
  [SerializeField] string glbFileName = "outmesh.glb";
  [SerializeField] Transform outmeshRoot;

  [Header("マテリアル適用")]
  [SerializeField] Material organMaterial;      // lung, heart など用（meshmate）
  [SerializeField] Material bodyMaterial;       // 体表用（M_Body_Transparent）
  [SerializeField] string bodyNameKey = "body"; // 「body」を含む階層を Body とみなす

  [Header("既存コンポーネント連携")]
  [SerializeField] PresetManager presetManager;
  [SerializeField] string initialPresetName;
  [SerializeField] GroupAlphaSlider groupAlphaSlider;
  [SerializeField] OrganAlphaSlider[] organAlphaSliders;

  [Header("XR Grab 設定")]
  [SerializeField] bool setupGrabInteractable = true;
  [SerializeField] bool addBoxColliderIfMissing = true;
  [SerializeField] bool fitColliderToModel = true;
  [SerializeField] bool addRigidbodyIfMissing = true;

  GltfImport _gltf;

  async void Awake()
  {
    if (outmeshRoot == null)
      outmeshRoot = transform;

    if (presetManager == null)
      presetManager = GetComponent<PresetManager>();

    var modelsDir = Path.Combine(Application.persistentDataPath, "models");
    var fullPath = Path.Combine(modelsDir, glbFileName);

    if (!File.Exists(fullPath))
    {
      Debug.LogError($"[OutmeshRuntimeLoader] GLB が見つかりません: {fullPath}");
      return;
    }

    _gltf = new GltfImport();

    bool loaded;
    try
    {
      loaded = await _gltf.LoadFile(fullPath);
    }
    catch (Exception e)
    {
      Debug.LogError($"[OutmeshRuntimeLoader] GLB 読み込み例外: {e}");
      return;
    }

    if (!loaded)
    {
      Debug.LogError($"[OutmeshRuntimeLoader] GLB 読み込みに失敗しました: {fullPath}");
      return;
    }

    bool instantiated;
    try
    {
      instantiated = await _gltf.InstantiateMainSceneAsync(outmeshRoot);
    }
    catch (Exception e)
    {
      Debug.LogError($"[OutmeshRuntimeLoader] InstantiateMainSceneAsync 例外: {e}");
      return;
    }

    if (!instantiated)
    {
      Debug.LogError("[OutmeshRuntimeLoader] InstantiateMainSceneAsync が false を返しました。");
      return;
    }

    // ここで URP マテリアルを適用
    ApplyDefaultMaterials();

    // XR Grab 設定
    if (setupGrabInteractable)
      SetupGrabComponents();

    // 透過スライダー用に対象 Renderer を再スキャン
    if (groupAlphaSlider != null)
      groupAlphaSlider.RefreshTargets();

    if (organAlphaSliders != null)
    {
      foreach (var s in organAlphaSliders)
        if (s != null) s.RefreshTargets();
    }
    // ここで初回プリセット適用
    if (presetManager != null && !string.IsNullOrEmpty(initialPresetName))
    {
      presetManager.ApplyPresetResource(initialPresetName);
    }

 
  }

  // ----------------------------------------------------------
  //  マテリアル適用（body=M_Body_Transparent / その他=meshmate）
  // ----------------------------------------------------------
  void ApplyDefaultMaterials()
  {
    bool hasBody = bodyMaterial != null;
    bool hasOrgan = organMaterial != null;
    if (!hasBody && !hasOrgan) return;

    string bodyKey = Normalize(bodyNameKey);

    var renderers = outmeshRoot.GetComponentsInChildren<Renderer>(true);
    foreach (var r in renderers)
    {
      bool isBody = hasBody && IsBodyRenderer(r, outmeshRoot, bodyKey);
      Material target = isBody ? bodyMaterial : organMaterial;
      if (target == null) continue;

      var shared = r.sharedMaterials;
      if (shared == null || shared.Length == 0) continue;

      for (int i = 0; i < shared.Length; i++)
        shared[i] = target;
      r.sharedMaterials = shared;
    }
  }

  static bool IsBodyRenderer(Renderer r, Transform root, string normalizedBodyKey)
  {
    if (string.IsNullOrEmpty(normalizedBodyKey)) return false;

    for (var t = r.transform; t != null && t != root.parent; t = t.parent)
    {
      if (Normalize(t.name).Contains(normalizedBodyKey))
        return true;
    }
    return false;
  }

  static string Normalize(string s)
  {
    if (string.IsNullOrEmpty(s)) return string.Empty;
    s = s.ToLowerInvariant();
    return s.Replace(" ", "").Replace("_", "").Replace("-", "");
  }

  // ----------------------------------------------------------
  //  XR Grab + XRDualGrabFreeTransformer
  // ----------------------------------------------------------
  void SetupGrabComponents()
  {
    var rootGO = outmeshRoot.gameObject;

    // ----- Collider & Rigidbody は今のままでOK -----
    Collider col = rootGO.GetComponent<Collider>();
    if (addBoxColliderIfMissing && col == null)
    {
      var box = rootGO.AddComponent<BoxCollider>();
      if (fitColliderToModel)
        FitBoxColliderToChildren(box, outmeshRoot);
      col = box;
    }
    else if (fitColliderToModel && col is BoxCollider existingBox)
    {
      FitBoxColliderToChildren(existingBox, outmeshRoot);
    }

    Rigidbody rb = rootGO.GetComponent<Rigidbody>();
    if (addRigidbodyIfMissing && rb == null)
    {
      rb = rootGO.AddComponent<Rigidbody>();
      rb.useGravity = false;
      rb.isKinematic = true;
    }

    // ----- XRGrabInteractable ＋ Transformer -----
    var grab = rootGO.GetComponent<XRGrabInteractable>();
    if (grab == null)
      grab = rootGO.AddComponent<XRGrabInteractable>();

    // 向きがリセットされないように Dynamic Attach 系を有効化
    grab.useDynamicAttach = true;                  // 掴むたびに動的なアタッチ点を計算
    grab.matchAttachPosition = true;               // コントローラ位置に合わせる
    grab.matchAttachRotation = true;               // コントローラ向きに合わせる
    grab.snapToColliderVolume = true;              // コライダー内にアタッチ
    grab.reinitializeDynamicAttachEverySingleGrab = true;
    // 「今の姿勢」を維持し、最初の姿勢に戻さない  

    grab.addDefaultGrabTransformers = false;
    grab.ClearSingleGrabTransformers();
    grab.ClearMultipleGrabTransformers();

    var transformer = rootGO.GetComponent<XRDualGrabFreeTransformer>();
    if (transformer == null)
      transformer = rootGO.AddComponent<XRDualGrabFreeTransformer>();

    grab.AddSingleGrabTransformer(transformer);
    grab.AddMultipleGrabTransformer(transformer);
  }

  /// <summary>
  /// outmeshRoot 配下の Renderer 全体を包むように BoxCollider をフィットさせる
  /// </summary>
  static void FitBoxColliderToChildren(BoxCollider box, Transform root)
  {
    var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
    if (renderers == null || renderers.Length == 0)
    {
      Debug.LogWarning("[OutmeshRuntimeLoader] Renderer が見つからないため BoxCollider をフィットできませんでした。");
      return;
    }

    bool hasBounds = false;
    Bounds localBounds = new Bounds();

    foreach (var r in renderers)
    {
      var b = r.bounds;
      var center = b.center;
      var extents = b.extents;

      for (int ix = -1; ix <= 1; ix += 2)
        for (int iy = -1; iy <= 1; iy += 2)
          for (int iz = -1; iz <= 1; iz += 2)
          {
            var worldCorner = center + Vector3.Scale(extents, new Vector3(ix, iy, iz));
            var localCorner = root.InverseTransformPoint(worldCorner);

            if (!hasBounds)
            {
              localBounds = new Bounds(localCorner, Vector3.zero);
              hasBounds = true;
            }
            else
            {
              localBounds.Encapsulate(localCorner);
            }
          }
    }

    box.center = localBounds.center;
    box.size = localBounds.size;
  }
}
