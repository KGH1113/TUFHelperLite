using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TUFHelperLite.Presentation.Unity;

internal sealed class ToastHoverTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
  private Action<bool> _hoverChanged;

  public void Configure(Action<bool> hoverChanged)
  {
    _hoverChanged = hoverChanged;
  }

  public void OnPointerEnter(PointerEventData eventData)
  {
    _hoverChanged?.Invoke(true);
  }

  public void OnPointerExit(PointerEventData eventData)
  {
    _hoverChanged?.Invoke(false);
  }

  private void OnDisable()
  {
    _hoverChanged?.Invoke(false);
  }
}
