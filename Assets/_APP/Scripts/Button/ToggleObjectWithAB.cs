using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 右コントローラの A/B ボタンで target を表示/非表示にする。
/// A(=primaryButton) → 表示、B(=secondaryButton) → 非表示。
/// </summary>
public sealed class ToggleObjectWithAB : MonoBehaviour
{
  [Tooltip("表示/非表示を切り替える対象。未設定ならこのGameObject")]
  public GameObject target;

  [Tooltip("起動時に表示しておくか")]
  public bool startsVisible = true;

  private InputAction showA; // A / primaryButton
  private InputAction hideB; // B / secondaryButton

  void OnEnable()
  {
    if (target == null) target = gameObject;

    // 右手A/B（複数レイアウトを束ねて確実に拾う）
    showA = new InputAction("ShowA", binding: "<XRController>{RightHand}/primaryButton");
    hideB = new InputAction("HideB", binding: "<XRController>{RightHand}/secondaryButton");
    // （保険）Oculus/Quest系レイアウトにも明示バインド
    showA.AddBinding("<OculusTouchController>{RightHand}/primaryButton");
    hideB.AddBinding("<OculusTouchController>{RightHand}/secondaryButton");
    showA.AddBinding("<MetaQuestTouchProController>{RightHand}/primaryButton");
    hideB.AddBinding("<MetaQuestTouchProController>{RightHand}/secondaryButton");
    // （任意）エディタ検証用のキーボード
    showA.AddBinding("<Keyboard>/k");
    hideB.AddBinding("<Keyboard>/l");

    showA.performed += _ => SetVisible(true);
    hideB.performed += _ => SetVisible(false);

    showA.Enable();
    hideB.Enable();

    SetVisible(startsVisible);
  }

  void OnDisable()
  {
    showA?.Dispose();
    hideB?.Dispose();
  }

  void SetVisible(bool on)
  {
    if (target) target.SetActive(on);
  }
}
