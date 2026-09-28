using System;
using System.Collections.Generic;
using System.Reflection;
using PhoenixPoint.Common.View.ViewControllers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Morgott.PPBridge
{
    /// <summary>
    /// The game half of <c>ui</c> (pure half + contract: Ui.cs). Scan = every active uGUI element a
    /// pointer click can reach: enabled Selectables (Selectable.allSelectablesArray) plus any enabled
    /// MonoBehaviour implementing IPointerClickHandler (PhoenixGeneralButton, list rows, TFTV panels).
    /// Click = the pointer sequence StandaloneInputModule runs for a real left click, started at the
    /// object the raycast at the element's centre actually hits: enter (hit and every ancestor ->
    /// hovered) -> deselect-if-changed -> down (ExecuteHierarchy) + pointerDrag/initializePotentialDrag
    /// -> up -> click (only when the press handler IS the click handler, like Unity) -> exit.
    /// PhoenixGeneralButton.OnPointerClick ignores a click unless eventData.hovered contains its
    /// BaseButton's GameObject - a hovered chain from the real hit contains it wherever it sits.
    /// </summary>
    internal static class UiGame
    {
        private static Type tmpType;
        private static PropertyInfo tmpText;
        private static bool tmpLooked;

        internal static void Install()
        {
            UiTap.Scan = Scan;
            UiTap.ClickRun = Click;
            UiTap.FrameNow = () => Time.frameCount;
        }

        private static List<UiNode> Scan()
        {
            List<UiNode> nodes = new List<UiNode>();
            HashSet<int> seen = new HashSet<int>();
            foreach (Selectable s in Selectable.allSelectablesArray)
                if (s != null && s.isActiveAndEnabled) Add(nodes, seen, s.gameObject);
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb is IPointerClickHandler && mb.isActiveAndEnabled && mb.transform is RectTransform) Add(nodes, seen, mb.gameObject);
            // A container that is itself clickable (UINavigationalElementsHolder, list views) would
            // otherwise borrow its first child button's text and collide with that button's label.
            HashSet<Transform> cands = new HashSet<Transform>();
            foreach (UiNode n in nodes) cands.Add(((GameObject)n.Ref).transform);
            foreach (UiNode n in nodes) n.Label = Label((GameObject)n.Ref, cands);
            nodes.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X) : string.CompareOrdinal(a.Path, b.Path));
            return nodes;
        }

        private static void Add(List<UiNode> nodes, HashSet<int> seen, GameObject go)
        {
            if (!seen.Add(go.GetInstanceID()) || !go.activeInHierarchy) return;
            RectTransform rt = go.transform as RectTransform;
            if (rt == null) return;
            Canvas canvas = go.GetComponentInParent<Canvas>();
            Rect r;
            bool onScreen = ScreenRect(rt, canvas, out r);
            PhoenixGeneralButton pgb = go.GetComponent<PhoenixGeneralButton>();
            Selectable sel = go.GetComponent<Selectable>();
            bool inter = sel == null || sel.IsInteractable();
            if (pgb != null && pgb.enabled && (!pgb.IsEnabled || (pgb.IsSelected && pgb.IsNonInteractableWhenSelected))) inter = false;
            string type = pgb != null && pgb.enabled ? "PhoenixGeneralButton" : sel != null ? sel.GetType().Name : ClickType(go);
            int top = Screen.height - Mathf.RoundToInt(r.yMax);
            nodes.Add(new UiNode
            {
                Path = PathOf(go.transform),
                Type = type,
                Interactable = inter,
                Visible = onScreen && canvas != null && canvas.isActiveAndEnabled && Alpha(go) > 0.01f,
                X = Mathf.RoundToInt(r.xMin), Y = top, W = Mathf.RoundToInt(r.width), H = Mathf.RoundToInt(r.height),
                Ref = go
            });
        }

        private static string ClickType(GameObject go)
        {
            foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
                if (mb is IPointerClickHandler && mb.isActiveAndEnabled) return mb.GetType().Name;
            return "?";
        }

        /// <summary>Screen-space rect (Unity pixels, Y up). False = zero-sized or off every pixel.</summary>
        private static bool ScreenRect(RectTransform rt, Canvas canvas, out Rect r)
        {
            Vector3[] c = new Vector3[4];
            rt.GetWorldCorners(c);
            Canvas root = canvas != null ? canvas.rootCanvas : null;
            Camera cam = root == null || root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, c[i]);
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return r.width >= 1 && r.height >= 1 && x1 > 0 && y1 > 0 && x0 < Screen.width && y0 < Screen.height;
        }

        /// <summary>Effective CanvasGroup alpha (stops at ignoreParentGroups, like Unity).</summary>
        private static float Alpha(GameObject go)
        {
            float a = 1f;
            foreach (CanvasGroup g in go.GetComponentsInParent<CanvasGroup>())
            {
                if (!g.enabled) continue;
                a *= g.alpha;
                if (g.ignoreParentGroups) break;
            }
            return a;
        }

        /// <summary>True when <paramref name="t"/> belongs to <paramref name="owner"/>, not to a clickable
        /// element nested between them.</summary>
        private static bool Owns(Transform owner, Transform t, HashSet<Transform> cands)
        {
            for (; t != null && t != owner; t = t.parent)
                if (cands.Contains(t)) return false;
            return t == owner;
        }

        private static string Label(GameObject go, HashSet<Transform> cands)
        {
            foreach (Text t in go.GetComponentsInChildren<Text>())
            {
                if (!Owns(go.transform, t.transform, cands)) continue;
                string s = UiTap.CleanText(t.text);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            if (!tmpLooked)
            {
                tmpLooked = true;
                tmpType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
                tmpText = tmpType == null ? null : tmpType.GetProperty("text");
            }
            if (tmpText != null)
                foreach (Component c in go.GetComponentsInChildren(tmpType))
                {
                    if (!Owns(go.transform, c.transform, cands)) continue;
                    string s = UiTap.CleanText(tmpText.GetValue(c, null) as string);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            return go.name;
        }

        internal static string PathOf(Transform t)
        {
            List<string> segs = new List<string>();
            for (; t != null; t = t.parent)
            {
                string n = t.name;
                int k = 0;
                Transform p = t.parent;
                if (p != null)
                    for (int i = 0; i < p.childCount; i++)
                    {
                        Transform sib = p.GetChild(i);
                        if (sib == t) break;
                        if (sib.name == n) k++;
                    }
                segs.Add(k > 0 ? n + "[" + k + "]" : n);
            }
            segs.Reverse();
            return string.Join("/", segs.ToArray());
        }

        private static UiClickResult Click(UiNode node, bool force)
        {
            GameObject go = node.Ref as GameObject;
            if (go == null || !go.activeInHierarchy) return new UiClickResult { Error = "the element is gone or inactive since the scan" };
            EventSystem es = EventSystem.current;
            if (es == null) return new UiClickResult { Error = "no EventSystem.current - no uGUI input in this scene" };
            RectTransform rt = (RectTransform)go.transform;
            Rect r;
            ScreenRect(rt, go.GetComponentInParent<Canvas>(), out r);
            Vector2 centre = r.center;

            PointerEventData ped = new PointerEventData(es)
            {
                button = PointerEventData.InputButton.Left,
                position = centre, pressPosition = centre,
                clickCount = 1, clickTime = Time.unscaledTime,
                eligibleForClick = true, useDragThreshold = true
            };

            UiClickResult res = new UiClickResult();
            List<RaycastResult> hits = new List<RaycastResult>();
            try { es.RaycastAll(ped, hits); } catch (Exception) { hits.Clear(); }
            RaycastResult first = default(RaycastResult);          // BaseInputModule.FindFirstRaycast
            foreach (RaycastResult h in hits) if (h.gameObject != null) { first = h; break; }
            GameObject top = first.gameObject;
            // Refuse BEFORE any event goes out: what a real mouse would not reach is not clicked.
            if (top == null)
            {
                if (!force) return new UiClickResult { Refuse = "noraycast" };
                res.Warn = "forced: noraycast - nothing raycastable at the centre, a real mouse click would miss";
            }
            else if (!top.transform.IsChildOf(go.transform) && ExecuteEvents.GetEventHandler<IPointerClickHandler>(top) != go)
            {
                if (!force) return new UiClickResult { Refuse = "blocked", Top = PathOf(top.transform) };
                res.Warn = "forced: blocked - " + UiTap.ShortPath(PathOf(top.transform)) + " is on top at the centre";
            }
            // Like StandaloneInputModule: everything starts at the object the raycast actually hit
            // (a child Image/Text of the button, typically) - forced past a refusal, at the element.
            bool fromHit = top != null && res.Warn == null;
            GameObject over = fromHit ? top : go;
            RaycastResult rr = fromHit ? first : new RaycastResult { gameObject = go, screenPosition = centre, module = first.module };
            ped.pointerCurrentRaycast = rr;

            // Who would take the press - checked BEFORE any event: it must be the element, inside it,
            // or an ancestor that owns it; anything else is a different element (blocked).
            GameObject handler = ExecuteEvents.GetEventHandler<IPointerDownHandler>(over) ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            if (handler == null) return new UiClickResult { Error = "nothing on the element or its parents handles pointerDown/pointerClick" };
            if (handler != go && !handler.transform.IsChildOf(go.transform) && !go.transform.IsChildOf(handler.transform))
            {
                if (!force) return new UiClickResult { Refuse = "blocked", Top = PathOf(handler.transform) };
                res.Warn = (res.Warn == null ? "forced: " : res.Warn + "; ") + "press goes to " + UiTap.ShortPath(PathOf(handler.transform));
            }

            // Enter (HandlePointerExitAndEnter from nothing): the hit and every ancestor, hit first.
            for (Transform t = over.transform; t != null; t = t.parent)
            {
                ExecuteEvents.Execute(t.gameObject, ped, ExecuteEvents.pointerEnterHandler);
                ped.hovered.Add(t.gameObject);
            }
            ped.pointerEnter = over;

            // Press (ProcessMousePress, PressedThisFrame).
            ped.eligibleForClick = true;
            ped.delta = Vector2.zero;
            ped.dragging = false;
            ped.useDragThreshold = true;
            ped.pressPosition = ped.position;
            ped.pointerPressRaycast = ped.pointerCurrentRaycast;
            GameObject selectH = ExecuteEvents.GetEventHandler<ISelectHandler>(over);
            if (selectH != es.currentSelectedGameObject) es.SetSelectedGameObject(null, ped);
            GameObject press = ExecuteEvents.ExecuteHierarchy(over, ped, ExecuteEvents.pointerDownHandler);
            if (press == null) press = ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            ped.clickCount = 1;
            ped.pointerPress = press;
            ped.rawPointerPress = over;
            ped.clickTime = Time.unscaledTime;
            ped.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(over);
            if (ped.pointerDrag != null) ExecuteEvents.Execute(ped.pointerDrag, ped, ExecuteEvents.initializePotentialDrag);

            // Release (ReleasedThisFrame): up to the press target, click only when it is also the
            // click handler of what is under the pointer - Unity's own rule.
            if (press != null) ExecuteEvents.Execute(press, ped, ExecuteEvents.pointerUpHandler);
            GameObject clickH = ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            if (press != null && press == clickH && ped.eligibleForClick)
            {
                ExecuteEvents.Execute(press, ped, ExecuteEvents.pointerClickHandler);
                res.Handler = "pointerClick";
            }
            else if (press != null) res.Handler = "pointerDown";
            else res.Error = "nothing on the element or its parents handles pointerDown/pointerClick";
            if (press != null && press != go) res.Target = PathOf(press.transform);

            ped.eligibleForClick = false;
            ped.pointerPress = null;
            ped.rawPointerPress = null;
            ped.dragging = false;
            ped.pointerDrag = null;
            // The pointer leaves again (a real one stays; ours must not leave hover state behind).
            foreach (GameObject h in ped.hovered.ToArray())
                if (h != null) ExecuteEvents.Execute(h, ped, ExecuteEvents.pointerExitHandler);
            ped.hovered.Clear();
            ped.pointerEnter = null;
            return res;
        }
    }
}
