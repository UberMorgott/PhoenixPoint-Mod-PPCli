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
    /// Click = the pointer sequence StandaloneInputModule runs for a real left click, executed on the
    /// element itself: enter (whole hovered chain) -> down (ExecuteHierarchy) -> up -> click (only when
    /// the press handler IS the click handler, like Unity) -> exit. PhoenixGeneralButton.OnPointerClick
    /// ignores a click unless eventData.hovered contains its BaseButton's GameObject - hence the chain.
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
                Label = Label(go),
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

        private static string Label(GameObject go)
        {
            foreach (Text t in go.GetComponentsInChildren<Text>())
            {
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

        private static UiClickResult Click(UiNode node)
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
            GameObject top = hits.Count > 0 ? hits[0].gameObject : null;
            if (top == null) res.Warn = "noraycast: nothing raycastable at the centre - a real mouse click would miss";
            else if (!top.transform.IsChildOf(go.transform) && ExecuteEvents.GetEventHandler<IPointerClickHandler>(top) != go)
                res.Warn = "blocked: " + UiTap.ShortPath(PathOf(top.transform)) + " is on top at the centre";
            RaycastResult rr = new RaycastResult { gameObject = go, screenPosition = centre, module = hits.Count > 0 ? hits[0].module : null };
            ped.pointerCurrentRaycast = rr;
            ped.pointerPressRaycast = rr;

            List<GameObject> chain = new List<GameObject>();
            for (Transform t = go.transform; t != null; t = t.parent) chain.Add(t.gameObject);
            ped.hovered.AddRange(chain);
            ped.pointerEnter = go;
            for (int i = chain.Count - 1; i >= 0; i--) ExecuteEvents.Execute(chain[i], ped, ExecuteEvents.pointerEnterHandler);

            GameObject press = ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
            GameObject clickH = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
            if (press == null) press = clickH;
            ped.pointerPress = press;
            ped.rawPointerPress = go;

            if (press != null) ExecuteEvents.Execute(press, ped, ExecuteEvents.pointerUpHandler);
            if (press != null && press == clickH)
            {
                ExecuteEvents.Execute(clickH, ped, ExecuteEvents.pointerClickHandler);
                res.Handler = "pointerClick";
            }
            else if (clickH == null)
            {
                Button b = go.GetComponent<Button>();
                if (b != null && b.IsInteractable()) { b.onClick.Invoke(); res.Handler = "onClick"; }
                else if (press != null) res.Handler = "pointerDown";
                else res.Error = "nothing on the element or its parents handles pointerDown/pointerClick";
            }
            else res.Handler = "pointerDown";     // Unity itself clicks only when down and click share a handler

            ped.eligibleForClick = false;
            ped.pointerPress = null;
            ped.rawPointerPress = null;
            foreach (GameObject h in chain)
                if (h != null) ExecuteEvents.Execute(h, ped, ExecuteEvents.pointerExitHandler);
            ped.hovered.Clear();
            ped.pointerEnter = null;
            return res;
        }
    }
}
