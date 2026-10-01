using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace SortedModConsole
{
    /// <summary>
    /// The console itself, drawn with IMGUI: a list of sources on the left (All, Problems, then one
    /// per mod), the filtered log on the right, and the full text of the clicked line below it.
    /// </summary>
    internal sealed class ConsoleWindow : MonoBehaviour
    {
        private const string TabAll = "\u0001All";
        private const string TabProblems = "\u0001Problems";

        private struct Row
        {
            public LogEntry Entry;
            public int Count;
            public DateTime? LastTime;
        }

        private readonly InputBlocker blocker = new InputBlocker();
        private bool open;

        // What is shown.
        private string tab = TabAll;
        private readonly bool[] showSeverity = { false, true, true, true }; // Debug, Info, Warning, Error
        private string search = "";
        private bool collapse;
        private LogEntry selected;

        // The filtered rows, rebuilt when the store or the filters change.
        private readonly List<Row> view = new List<Row>();
        private int viewVersion = -1;
        private string viewKey;
        private float lastRebuild;

        private List<SourceStats> sortedSources = new List<SourceStats>();
        private int sortedVersion = -1;

        private Vector2 listScroll, sideScroll, detailScroll;
        // Follow new lines only while the list is scrolled to the bottom.
        private bool stickToBottom = true;

        // Styles, sized for the current screen.
        private float scale;
        private int styleFontSize = -1;
        private GUIStyle label, rowText, rowDim, rowDimRight, sideButton, sideButtonOn, toolButton, toolButtonOn, field, detailText, header;
        private readonly GUIStyle[] severityText = new GUIStyle[4];
        private Texture2D texPanel, texSide, texRowAlt, texRowSelected, texDetail, texButton, texButtonOn;

        private static readonly Color[] severityColor =
        {
            new Color(0.55f, 0.58f, 0.62f), // Debug
            new Color(0.86f, 0.87f, 0.89f), // Info
            new Color(1.00f, 0.80f, 0.35f), // Warning
            new Color(1.00f, 0.42f, 0.40f), // Error
        };
        private static readonly string[] severityName = { "Debug", "Info", "Warnings", "Errors" };

        private void Update()
        {
            LogStore.Drain();

            if (HotkeyPressed())
                SetOpen(!open);
            else if (open && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                SetOpen(false);

            if (open)
                blocker.Tick();
        }

        private void LateUpdate()
        {
            // The game re-locks the cursor in its own Update on some screens.
            if (open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void OnDestroy() => blocker.Release();

        private static bool HotkeyPressed()
        {
            var key = Plugin.hotkey.Value;
            if (key == Key.None || Keyboard.current == null)
                return false;
            try { return Keyboard.current[key].wasPressedThisFrame; }
            catch { return false; }
        }

        private void SetOpen(bool value)
        {
            if (open == value)
                return;
            open = value;
            if (open)
            {
                blocker.Engage();
                stickToBottom = true;
            }
            else
            {
                blocker.Release();
                GUIUtility.keyboardControl = 0;
            }
        }

        // ---------------------------------------------------------------- drawing

        private float U(float v) => v * scale;

        private void OnGUI()
        {
            GUI.depth = -1000;
            scale = Screen.height / 1080f * Mathf.Clamp(Plugin.uiScale.Value, 0.5f, 3f);
            EnsureStyles();

            if (!open)
            {
                if (Plugin.menuButton.Value && SceneManager.GetActiveScene().name == "MainMenu")
                {
                    var r = new Rect(Screen.width - U(190), U(12), U(178), U(30));
                    if (GUI.Button(r, $"Mod Console ({Plugin.hotkey.Value})", toolButton))
                        SetOpen(true);
                }
                return;
            }

            float m = U(28);
            var panel = new Rect(m, m, Screen.width - 2 * m, Screen.height - 2 * m);
            GUI.DrawTexture(panel, texPanel);

            float pad = U(8);
            float y = panel.y + pad;
            float x0 = panel.x + pad, x1 = panel.xMax - pad;

            // Title row.
            float h = U(28);
            GUI.Label(new Rect(x0, y, U(300), h), "Sorted Mod Console", header);
            GUI.Label(new Rect(x0 + U(215), y, U(400), h), $"{Plugin.hotkey.Value} or Esc to close", rowDim);
            if (GUI.Button(new Rect(x1 - U(80), y, U(80), h), "Close", toolButton))
                SetOpen(false);
            y += h + pad;

            DrawToolbar(new Rect(x0, y, x1 - x0, h));
            y += h + pad;

            float sideW = U(280);
            var side = new Rect(x0, y, sideW, panel.yMax - pad - y);
            DrawSidebar(side);

            RebuildViewIfNeeded();

            var right = new Rect(side.xMax + pad, y, x1 - side.xMax - pad, side.height);

            string dropped = DroppedNotice();
            if (dropped != null)
            {
                float nh = U(24);
                GUI.Label(new Rect(right.x + U(6), right.y, right.width - U(12), nh), dropped, rowDim);
                right.yMin += nh + U(2);
            }

            if (selected != null)
            {
                float detailH = Mathf.Round(right.height * 0.32f);
                var list = new Rect(right.x, right.y, right.width, right.height - detailH - pad);
                DrawList(list);
                DrawDetail(new Rect(right.x, list.yMax + pad, right.width, detailH));
            }
            else
            {
                DrawList(right);
            }
        }

        private void DrawToolbar(Rect r)
        {
            float x = r.x, gap = U(6);

            for (int i = 3; i >= 0; i--)
            {
                float w = U(i == 1 ? 60 : 92);
                bool on = showSeverity[i];
                if (GUI.Button(new Rect(x, r.y, w, r.height), severityName[i], on ? toolButtonOn : toolButton))
                    showSeverity[i] = !on;
                x += w + gap;
            }

            x += gap;
            if (GUI.Button(new Rect(x, r.y, U(92), r.height), "Collapse", collapse ? toolButtonOn : toolButton))
                collapse = !collapse;
            x += U(92) + gap * 3;

            GUI.Label(new Rect(x, r.y, U(56), r.height), "Search", label);
            x += U(56);
            float fieldW = U(260);
            search = GUI.TextField(new Rect(x, r.y, fieldW, r.height), search, field);
            x += fieldW;
            if (search.Length > 0 && GUI.Button(new Rect(x + U(2), r.y, U(28), r.height), "x", toolButton))
            {
                search = "";
                GUIUtility.keyboardControl = 0;
            }

            float bw = U(110);
            float rx = r.xMax - bw;
            if (GUI.Button(new Rect(rx, r.y, bw, r.height), "Clear tab", toolButton))
                ClearTab();
            rx -= bw + gap;
            if (GUI.Button(new Rect(rx, r.y, bw, r.height), "Copy tab", toolButton))
                GUIUtility.systemCopyBuffer = ViewAsText();
        }

        private void DrawSidebar(Rect r)
        {
            GUI.DrawTexture(r, texSide);

            if (sortedVersion != LogStore.SourcesVersion)
            {
                sortedSources = LogStore.Sources.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                sortedVersion = LogStore.SourcesVersion;
            }

            float rowH = U(26);
            int rows = sortedSources.Count + 3; // All, Problems, a gap
            var content = new Rect(0, 0, r.width - U(16), rows * rowH);
            sideScroll = GUI.BeginScrollView(r, sideScroll, content, false, false);

            int warnings = 0, errors = 0;
            foreach (var s in sortedSources)
            {
                warnings += s.Warnings;
                errors += s.Errors;
            }

            float y = 0;
            SideRow(new Rect(0, y, content.width, rowH), TabAll, "All", LogStore.Sources.Values.Sum(s => s.Total), 0, 0);
            y += rowH;
            SideRow(new Rect(0, y, content.width, rowH), TabProblems, "Problems", warnings + errors, warnings, errors);
            y += rowH * 2;
            foreach (var s in sortedSources)
            {
                if (y + rowH >= sideScroll.y && y <= sideScroll.y + r.height)
                    SideRow(new Rect(0, y, content.width, rowH), s.Name, s.Name, s.Total, s.Warnings, s.Errors);
                y += rowH;
            }

            GUI.EndScrollView();
        }

        private void SideRow(Rect r, string key, string name, int total, int warnings, int errors)
        {
            bool on = tab == key;
            if (GUI.Button(r, GUIContent.none, on ? sideButtonOn : sideButton))
                SelectTab(key);

            var inner = new Rect(r.x + U(8), r.y, r.width - U(16), r.height);
            string counts = total.ToString();
            float countW = U(58);
            var nameStyle = errors > 0 ? severityText[3] : warnings > 0 ? severityText[2] : rowText;
            // A gap before the count, so a long name is cut off instead of running into it.
            GUI.Label(new Rect(inner.x, inner.y, inner.width - countW - U(8), inner.height), name, nameStyle);
            GUI.Label(new Rect(inner.xMax - countW, inner.y, countW, inner.height), counts, rowDimRight);
        }

        private void SelectTab(string key)
        {
            if (tab == key)
                return;
            tab = key;
            selected = null;
            stickToBottom = true;
        }

        private void DrawList(Rect r)
        {
            float rowH = Mathf.Round(Plugin.fontSize.Value * scale * 1.55f);
            float contentH = view.Count * rowH;
            float maxY = Mathf.Max(0, contentH - r.height);
            if (stickToBottom)
                listScroll.y = maxY;

            float before = listScroll.y;
            var content = new Rect(0, 0, r.width - U(16), contentH);
            listScroll = GUI.BeginScrollView(r, listScroll, content, false, true);

            bool showSource = tab == TabAll || tab == TabProblems;
            float timeW = U(76), sourceW = showSource ? U(190) : 0, pad = U(6);
            int first = Mathf.Max(0, (int)(listScroll.y / rowH));
            int last = Mathf.Min(view.Count - 1, (int)((listScroll.y + r.height) / rowH) + 1);

            for (int i = first; i <= last; i++)
            {
                var row = view[i];
                var e = row.Entry;
                var rr = new Rect(0, i * rowH, content.width, rowH);

                if (e == selected)
                    GUI.DrawTexture(rr, texRowSelected);
                else if ((i & 1) == 1)
                    GUI.DrawTexture(rr, texRowAlt);

                if (GUI.Button(rr, GUIContent.none, GUIStyle.none))
                {
                    selected = selected == e ? null : e;
                    detailScroll = Vector2.zero;
                }

                float x = pad;
                GUI.Label(new Rect(x, rr.y, timeW, rowH), FormatTime(row.LastTime), rowDim);
                x += timeW;
                if (showSource)
                {
                    GUI.Label(new Rect(x, rr.y, sourceW - pad, rowH), e.Source, rowDim);
                    x += sourceW;
                }

                string text = e.FirstLine;
                if (e.ExtraLines > 0)
                    text += $"   (+{e.ExtraLines} lines)";
                if (row.Count > 1)
                    text = $"×{row.Count}  {text}";
                GUI.Label(new Rect(x, rr.y, rr.width - x - pad, rowH), text, severityText[(int)e.Severity]);
            }

            GUI.EndScrollView();

            // The user scrolled: stay where they are unless they went back to the bottom.
            if (!Mathf.Approximately(listScroll.y, before))
                stickToBottom = listScroll.y >= maxY - rowH * 0.5f;

            if (view.Count == 0)
                GUI.Label(new Rect(r.x + U(12), r.y + U(8), r.width, U(24)), "Nothing here with these filters.", rowDim);

            if (!stickToBottom)
            {
                var jr = new Rect(r.xMax - U(16) - U(150), r.yMax - U(34), U(142), U(28));
                if (GUI.Button(jr, "▼ Jump to latest", toolButtonOn))
                    stickToBottom = true;
            }
        }

        private void DrawDetail(Rect r)
        {
            GUI.DrawTexture(r, texDetail);
            var e = selected;
            float pad = U(8), h = U(26);

            string when = e.Time == null ? "during startup" : e.Time.Value.ToString("HH:mm:ss.fff");
            string head = $"{e.Level}   ·   {e.Source}   ·   {when}" + (e.Count > 1 ? $"   ·   repeated ×{e.Count}" : "");
            GUI.Label(new Rect(r.x + pad, r.y + U(4), r.width - U(200), h), head, severityText[(int)e.Severity]);
            if (GUI.Button(new Rect(r.xMax - pad - U(80), r.y + U(4), U(80), h - U(2)), "Close", toolButton))
            {
                selected = null;
                return;
            }
            if (GUI.Button(new Rect(r.xMax - pad - U(170), r.y + U(4), U(84), h - U(2)), "Copy", toolButton))
                GUIUtility.systemCopyBuffer = e.Message;

            var area = new Rect(r.x + pad, r.y + h + U(8), r.width - 2 * pad, r.height - h - U(14));
            float textW = area.width - U(18);
            float textH = Mathf.Max(area.height, detailText.CalcHeight(new GUIContent(e.Message), textW));
            detailScroll = GUI.BeginScrollView(area, detailScroll, new Rect(0, 0, textW, textH), false, false);
            // A text area so the text can be selected; edits are thrown away.
            GUI.TextArea(new Rect(0, 0, textW, textH), e.Message, detailText);
            GUI.EndScrollView();
        }

        private static string FormatTime(DateTime? t) => t == null ? "startup" : t.Value.ToString("HH:mm:ss");

        // ---------------------------------------------------------------- filtering

        private bool InTab(LogEntry e)
        {
            if (tab == TabAll) return true;
            if (tab == TabProblems) return e.Severity >= Severity.Warning;
            return e.Source == tab;
        }

        private void RebuildViewIfNeeded()
        {
            if (Event.current.type != EventType.Layout)
                return;

            string key = $"{tab}|{showSeverity[0]}{showSeverity[1]}{showSeverity[2]}{showSeverity[3]}|{collapse}|{search}";
            bool filtersChanged = key != viewKey;
            // New lines alone rebuild at most ten times a second, so a spamming mod stays cheap.
            if (!filtersChanged && (viewVersion == LogStore.Version || Time.unscaledTime - lastRebuild < 0.1f))
                return;

            viewKey = key;
            viewVersion = LogStore.Version;
            lastRebuild = Time.unscaledTime;
            view.Clear();

            var groups = collapse ? new Dictionary<(string, Severity, string), int>() : null;
            bool searching = search.Length > 0;

            foreach (var e in LogStore.Entries)
            {
                if (e.Dropped || !InTab(e) || !showSeverity[(int)e.Severity])
                    continue;
                if (searching
                    && e.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                    && e.Source.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (groups != null)
                {
                    var g = (e.Source, e.Severity, e.Message);
                    if (groups.TryGetValue(g, out int at))
                    {
                        var row = view[at];
                        row.Count += e.Count;
                        row.LastTime = e.LastTime;
                        view[at] = row;
                        continue;
                    }
                    groups[g] = view.Count;
                }
                view.Add(new Row { Entry = e, Count = e.Count, LastTime = e.LastTime });
            }

            if (selected != null && selected.Dropped)
                selected = null;
        }

        /// <summary>A line above the list when the limits pushed older lines of this tab out.</summary>
        private string DroppedNotice()
        {
            int lines = 0, problems = 0;
            if (tab == TabAll || tab == TabProblems)
            {
                foreach (var s in LogStore.Sources.Values)
                {
                    lines += s.DroppedLines;
                    problems += s.DroppedProblems;
                }
                if (tab == TabProblems)
                    lines = 0;
            }
            else if (LogStore.Sources.TryGetValue(tab, out var src))
            {
                lines = src.DroppedLines;
                problems = src.DroppedProblems;
            }

            if (lines + problems == 0)
                return null;
            string text = $"{lines + problems:N0} older lines dropped by the limits";
            if (problems > 0)
                text += $" ({problems:N0} of them warnings or errors)";
            if (tab != TabAll && tab != TabProblems)
                text += $". Kept per mod: {Plugin.maxLinesPerMod.Value:N0} lines, {Plugin.maxProblemsPerMod.Value:N0} warnings and errors.";
            return text + " The full log is in BepInEx/LogOutput.log.";
        }

        private void ClearTab()
        {
            if (tab == TabAll)
                LogStore.RemoveAll(_ => true);
            else
                LogStore.RemoveAll(InTab);
            selected = null;
            stickToBottom = true;
        }

        private string ViewAsText()
        {
            var sb = new StringBuilder();
            foreach (var row in view)
            {
                var e = row.Entry;
                sb.Append('[').Append(FormatTime(row.LastTime)).Append("] [")
                  .Append(e.Level).Append(" : ").Append(e.Source).Append("] ");
                if (row.Count > 1)
                    sb.Append("(×").Append(row.Count).Append(") ");
                sb.Append(e.Message).Append('\n');
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- styles

        private void EnsureStyles()
        {
            int fs = Mathf.Max(8, Mathf.RoundToInt(Plugin.fontSize.Value * scale));
            if (fs == styleFontSize && texPanel != null)
                return;
            styleFontSize = fs;

            if (texPanel == null)
            {
                texPanel = Tex(new Color(0.07f, 0.075f, 0.09f, 0.97f));
                texSide = Tex(new Color(0.10f, 0.105f, 0.125f, 1f));
                texRowAlt = Tex(new Color(1f, 1f, 1f, 0.025f));
                texRowSelected = Tex(new Color(0.30f, 0.45f, 0.75f, 0.35f));
                texDetail = Tex(new Color(0.11f, 0.115f, 0.14f, 1f));
                texButton = Tex(new Color(0.18f, 0.19f, 0.22f, 1f));
                texButtonOn = Tex(new Color(0.26f, 0.38f, 0.62f, 1f));
            }

            label = new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleLeft, richText = false };
            label.normal.textColor = severityColor[1];

            header = new GUIStyle(label) { fontSize = Mathf.RoundToInt(fs * 1.25f), fontStyle = FontStyle.Bold };

            rowText = new GUIStyle(label) { wordWrap = false, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 0, 0) };
            rowDim = new GUIStyle(rowText);
            rowDim.normal.textColor = new Color(0.52f, 0.55f, 0.60f);
            rowDimRight = new GUIStyle(rowDim) { alignment = TextAnchor.MiddleRight };
            for (int i = 0; i < 4; i++)
            {
                severityText[i] = new GUIStyle(rowText);
                severityText[i].normal.textColor = severityColor[i];
            }

            toolButton = Button(texButton);
            toolButtonOn = Button(texButtonOn);

            sideButton = new GUIStyle(GUIStyle.none);
            sideButton.hover.background = texRowAlt;
            sideButtonOn = new GUIStyle(GUIStyle.none);
            sideButtonOn.normal.background = texRowSelected;
            sideButtonOn.hover.background = texRowSelected;

            field = new GUIStyle(GUI.skin.textField) { fontSize = fs, alignment = TextAnchor.MiddleLeft };

            detailText = new GUIStyle(GUI.skin.label) { fontSize = fs, wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
            detailText.normal.textColor = severityColor[1];
            detailText.focused.textColor = severityColor[1];
            detailText.hover.textColor = severityColor[1];
            detailText.active.textColor = severityColor[1];
        }

        private GUIStyle Button(Texture2D bg)
        {
            var s = new GUIStyle(GUI.skin.button) { fontSize = styleFontSize, alignment = TextAnchor.MiddleCenter };
            s.normal.background = bg;
            s.hover.background = bg;
            s.active.background = texButtonOn;
            s.focused.background = bg;
            s.onNormal.background = bg;
            s.border = new RectOffset(0, 0, 0, 0);
            foreach (var st in new[] { s.normal, s.hover, s.active, s.focused })
                st.textColor = new Color(0.9f, 0.91f, 0.93f);
            return s;
        }

        private static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
