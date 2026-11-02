using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace ModbusSimulator
{
    /// <summary>
    /// 即时界面语言：0中文、1英语、2日语、3韩语、4俄语。只翻译显示层，
    /// 不更改线程区域设置、模型、Tag、输入框、串口名称或通信枚举值。
    /// 由宿主保存 Current；主窗、独立浮窗及模态对话框构造完成后调用 Apply。
    /// </summary>
    public static class UiLanguage
    {
        private sealed class TextState { public string Source, Rendered; public bool Changing; }
        private sealed class ControlState
        {
            public readonly TextState Text = new TextState(), Accessible = new TextState();
            public bool ComboDraw, GridHook;
        }
        private sealed class ColumnState { public readonly TextState Header = new TextState(), Tip = new TextState(); }
        private sealed class TipState { public readonly ConditionalWeakTable<Control, TextState> Controls = new ConditionalWeakTable<Control, TextState>(); }
        private static readonly ConditionalWeakTable<Control, ControlState> controls = new ConditionalWeakTable<Control, ControlState>();
        private static readonly ConditionalWeakTable<ToolStripItem, TextState> items = new ConditionalWeakTable<ToolStripItem, TextState>();
        private static readonly ConditionalWeakTable<DataGridViewColumn, ColumnState> columns = new ConditionalWeakTable<DataGridViewColumn, ColumnState>();
        private static readonly ConditionalWeakTable<ToolTip, TipState> tips = new ConditionalWeakTable<ToolTip, TipState>();
        private static readonly List<WeakReference> roots = new List<WeakReference>(), toolTips = new List<WeakReference>();
        private static readonly Dictionary<string, string[]> words = BuildWords();
        private static readonly string[] fragments = BuildFragments();
        private static int current;
        public static int Current { get { return current; } }

        /// <summary>从UI线程调用。非法索引回退中文，避免损坏的本地设置阻止启动。</summary>
        public static void SetLanguage(int language)
        {
            current = language >= 0 && language <= 4 ? language : 0;
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                Control root = roots[i].Target as Control;
                if (root == null || root.IsDisposed) { roots.RemoveAt(i); continue; }
                ApplyTree(root);
            }
        }

        /// <summary>注册控件树及其动态子控件；弱引用不延长已关闭窗口的寿命。</summary>
        public static void Apply(Control root)
        {
            if (root == null || root.IsDisposed) return;
            bool found = false;
            foreach (WeakReference reference in roots) if (ReferenceEquals(reference.Target, root)) { found = true; break; }
            if (!found) roots.Add(new WeakReference(root));
            ApplyTree(root);
        }

        public static string Translate(string text)
        {
            if (String.IsNullOrEmpty(text) || current == 0) return text;
            string[] row;
            if (words.TryGetValue(text, out row)) return row[current];
            // 组合状态包含地址、计数和协议标识，只替换明确登记的中文UI片段。
            // 单次扫描避免对译文再次替换；未知字符串原样保留。
            System.Text.StringBuilder result = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length; )
            {
                string match = null;
                foreach (string key in fragments)
                    if (text[i] == key[0] && i + key.Length <= text.Length && String.CompareOrdinal(text, i, key, 0, key.Length) == 0) { match = key; break; }
                if (match == null) { result.Append(text[i]); i++; }
                else { result.Append(words[match][current]); i += match.Length; }
            }
            return result.ToString();
        }

        private static void ApplyTree(Control control)
        {
            if (control == null || control.IsDisposed) return;
            ControlState state;
            if (!controls.TryGetValue(control, out state))
            {
                state = new ControlState(); controls.Add(control, state);
                control.TextChanged += TextChanged;
                control.ControlAdded += ChildAdded;
                control.ContextMenuStripChanged += ContextMenuChanged;
                control.MouseEnter += MouseEntered;
                DiscoverToolTips(control);
            }
            ApplyText(control, state);
            ComboBox combo = control as ComboBox;
            if (combo != null && IsDisplayCombo(combo))
            {
                if (!state.ComboDraw) { state.ComboDraw = true; combo.DrawMode = DrawMode.OwnerDrawFixed; combo.DrawItem += DrawCombo; }
                int width = combo.Width;
                foreach (object value in combo.Items) width = Math.Max(width, TextRenderer.MeasureText(Translate(Convert.ToString(value)), combo.Font).Width + 34);
                combo.DropDownWidth = Math.Min(Math.Max(combo.DropDownWidth, width), Math.Max(200, Screen.FromControl(combo).WorkingArea.Width - 40));
                combo.Invalidate();
            }
            DataGridView grid = control as DataGridView;
            if (grid != null)
            {
                if (!state.GridHook) { state.GridHook = true; grid.ColumnAdded += ColumnAdded; grid.CellFormatting += FormatCell; }
                foreach (DataGridViewColumn column in grid.Columns) ApplyColumn(column);
                grid.Invalidate();
            }
            ToolStrip strip = control as ToolStrip;
            if (strip != null)
            {
                strip.ItemAdded -= StripItemAdded; strip.ItemAdded += StripItemAdded;
                foreach (ToolStripItem item in strip.Items) ApplyItem(item);
            }
            if (control.ContextMenuStrip != null) ApplyTree(control.ContextMenuStrip);
            TranslateToolTips(control);
            foreach (Control child in control.Controls) ApplyTree(child);
        }

        private static string Render(TextState state, string value)
        {
            if (state.Source == null || value != state.Rendered) state.Source = value;
            state.Rendered = Translate(state.Source); return state.Rendered;
        }
        private static void ApplyText(Control control, ControlState state)
        {
            if (state.Text.Changing) return;
            state.Text.Changing = true;
            try
            {
                // 所有文本/数值输入完全跳过，包括正在编辑的名称、单位、原始报文及日志。
                if (!(control is TextBoxBase) && !(control is UpDownBase) && !(control is ComboBox) && !IsUserDisplay(control))
                {
                    string rendered = Render(state.Text, control.Text);
                    if (control.Text != rendered) control.Text = rendered;
                    Label label = control as Label; if (label != null && !label.AutoSize) label.AutoEllipsis = true;
                    ButtonBase button = control as ButtonBase; if (button != null) button.AutoEllipsis = true;
                }
                string accessible = Render(state.Accessible, control.AccessibleName);
                if (control.AccessibleName != accessible) control.AccessibleName = accessible;
            }
            finally { state.Text.Changing = false; }
        }
        private static bool IsUserDisplay(Control control)
        {
            // 卡片数值可能带用户自定义中文单位；卡片标题可能是任意名称，不能按词典猜测。
            Control card = control.Parent;
            if (card == null || card.GetType().Name != "AddressCard") return false;
            Type type = card.GetType(); FieldInfo field = type.GetField("ValueLabel");
            if (field != null && ReferenceEquals(field.GetValue(card), control))
            {
                ControlState state;
                bool parseError = control.Text == "无法解析" || (controls.TryGetValue(control, out state) && state.Text.Source == "无法解析" && state.Text.Rendered == control.Text);
                return !parseError;
            }
            field = type.GetField("Header");
            if (field == null || !ReferenceEquals(field.GetValue(card), control)) return false;
            try
            {
                object area = type.GetField("area", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(card);
                object presentation = area.GetType().GetField("Presentation").GetValue(area);
                object address = type.GetField("Address").GetValue(card);
                string name = (string)presentation.GetType().GetMethod("GetName").Invoke(presentation, new object[] { address });
                return !String.IsNullOrEmpty(name);
            }
            catch (Exception) { return true; }
        }
        internal static bool IsDisplayCombo(ComboBox combo)
        {
            if (combo.DropDownStyle != ComboBoxStyle.DropDownList || combo.Items.Count == 0) return false;
            string first = Convert.ToString(combo.Items[0]);
            // 只登记按索引消费的显示枚举，保留Parity、ParseRule、目标和原生语言名称。
            return first == "列表" || first == "串口 · RTU" || first == "保持寄存器 · 03 / 06 / 10" ||
                first == "位值 0 / 1" || first == "无符号十进制" || first == "无校验" ||
                first == "低字节在前" || first == "无换行" || first == "05 · 写单个线圈" ||
                first == "无符号 / 0x十六进制" || first == "白天" || first == "明亮模式";
        }
        /// <summary>供主题的原生下拉补绘复用；仅安全显示枚举翻译，目标及协议文本原样。</summary>
        public static string ComboDisplayText(ComboBox combo, object value)
        {
            string text = Convert.ToString(value);
            return combo != null && IsDisplayCombo(combo) ? Translate(text) : text;
        }
        private static void DrawCombo(object sender, DrawItemEventArgs e)
        {
            ComboBox combo = (ComboBox)sender;
            if (UiTheme.IsDark)
            {
                Color back = (e.State & DrawItemState.Selected) != 0 ? UiTheme.SelectedSurface : UiTheme.InputBackground;
                using (Brush brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
            }
            else e.DrawBackground();
            string text = e.Index >= 0 && e.Index < combo.Items.Count ? Convert.ToString(combo.Items[e.Index]) : combo.Text;
            Color color = UiTheme.IsDark ? (combo.Enabled ? UiTheme.Text : UiTheme.MutedText) : (combo.Enabled ? e.ForeColor : SystemColors.GrayText);
            Rectangle bounds = e.Bounds; bounds.Inflate(-2, 0);
            TextRenderer.DrawText(e.Graphics, ComboDisplayText(combo, text), e.Font ?? combo.Font, bounds, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }
        private static void TextChanged(object sender, EventArgs e) { Control c = sender as Control; ControlState s; if (c != null && controls.TryGetValue(c, out s)) ApplyText(c, s); }
        private static void ChildAdded(object sender, ControlEventArgs e) { ApplyTree(e.Control); }
        private static void ContextMenuChanged(object sender, EventArgs e) { Control c = sender as Control; if (c != null && c.ContextMenuStrip != null) ApplyTree(c.ContextMenuStrip); }
        private static void MouseEntered(object sender, EventArgs e) { TranslateToolTips((Control)sender); }
        private static void ColumnAdded(object sender, DataGridViewColumnEventArgs e) { ApplyColumn(e.Column); }
        private static void ApplyColumn(DataGridViewColumn column)
        {
            ColumnState state = columns.GetValue(column, delegate { return new ColumnState(); });
            column.HeaderText = Render(state.Header, column.HeaderText);
            column.ToolTipText = Render(state.Tip, column.ToolTipText);
        }
        private static void FormatCell(object sender, DataGridViewCellFormattingEventArgs e)
        {
            DataGridView grid = (DataGridView)sender;
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.IsDisposed || grid.Disposing) return;
            string name = grid.Columns[e.ColumnIndex].Name;
            if (name == "Area") { e.Value = Translate(Convert.ToString(e.Value)); e.FormattingApplied = true; }
            // 既有CellFormatting先生成默认标题；仅原始空名称才可翻译，用户自定义名称原样显示。
            else if (name == "Name" && String.IsNullOrEmpty(Convert.ToString(grid[e.ColumnIndex, e.RowIndex].Value)))
            { e.Value = Translate(Convert.ToString(e.Value)); e.FormattingApplied = true; }
        }
        private static void StripItemAdded(object sender, ToolStripItemEventArgs e) { ApplyItem(e.Item); }
        private static void ItemTextChanged(object sender, EventArgs e) { ApplyItem((ToolStripItem)sender); }
        private static void ApplyItem(ToolStripItem item)
        {
            TextState state;
            if (!items.TryGetValue(item, out state)) { state = new TextState(); items.Add(item, state); item.TextChanged += ItemTextChanged; }
            if (state.Changing) return; state.Changing = true;
            try { item.Text = Render(state, item.Text); item.ToolTipText = item.Text; }
            finally { state.Changing = false; }
            ToolStripDropDownItem menu = item as ToolStripDropDownItem;
            if (menu != null) { ApplyTree(menu.DropDown); foreach (ToolStripItem child in menu.DropDownItems) ApplyItem(child); }
        }
        private static void DiscoverToolTips(Control owner)
        {
            for (Type type = owner.GetType(); type != null && type != typeof(Control); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(ToolTip).IsAssignableFrom(field.FieldType)) continue;
                    ToolTip tip = field.GetValue(owner) as ToolTip; if (tip == null) continue;
                    TipState state; if (!tips.TryGetValue(tip, out state)) { tips.Add(tip, new TipState()); toolTips.Add(new WeakReference(tip)); }
                }
        }
        private static void TranslateToolTips(Control control)
        {
            for (int i = toolTips.Count - 1; i >= 0; i--)
            {
                ToolTip tip = toolTips[i].Target as ToolTip;
                if (tip == null) { toolTips.RemoveAt(i); continue; }
                string text = tip.GetToolTip(control);
                if (String.IsNullOrEmpty(text)) continue;
                TextState state = tips.GetValue(tip, delegate { return new TipState(); }).Controls.GetValue(control, delegate { return new TextState(); });
                string rendered = Render(state, text); if (rendered != text) tip.SetToolTip(control, rendered);
            }
        }
        private static string[] BuildFragments()
        {
            List<string> result = new List<string>();
            foreach (string key in words.Keys) if (key.Length >= 2 && key.IndexOf('\r') < 0 && key.IndexOf('\n') < 0) result.Add(key);
            result.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); }); return result.ToArray();
        }
        private static void Add(Dictionary<string, string[]> d, string zh, string en, string ja, string ko, string ru)
        { d.Add(zh, new string[] { zh, en, ja, ko, ru }); }
        private static Dictionary<string, string[]> BuildWords()
        {
            Dictionary<string, string[]> d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Add(d, "Modbus 从机模拟器", "Modbus Slave Simulator", "Modbus スレーブシミュレーター", "Modbus 슬레이브 시뮬레이터", "Симулятор Modbus Slave");
            Add(d, "Modbus从机模拟器", "Modbus Slave Simulator", "Modbus スレーブシミュレーター", "Modbus 슬레이브 시뮬레이터", "Симулятор Modbus Slave");
            Add(d, "Modbus 主机调试工具", "Modbus Master Tool", "Modbus マスターツール", "Modbus 마스터 도구", "Инструмент Modbus Master");
            Add(d, "Modbus主机调试工具", "Modbus Master Tool", "Modbus マスターツール", "Modbus 마스터 도구", "Инструмент Modbus Master");
            Add(d, "Modbus 从机", "Modbus Slave", "Modbus スレーブ", "Modbus 슬레이브", "Modbus Slave");
            Add(d, "连接设置", "Connection", "接続設定", "연결 설정", "Подключение");
            Add(d, "保存配置", "Save", "保存", "저장", "Сохранить");
            Add(d, "加载配置", "Load", "読込", "불러오기", "Загрузить");
            Add(d, "通信模式", "Mode", "通信方式", "통신 모드", "Режим");
            Add(d, "网络端口", "Port", "ポート", "포트", "Порт");
            Add(d, "站号", "Unit ID", "局番", "장치 ID", "Адрес");
            Add(d, "波特率", "Baud", "ボーレート", "전송 속도", "Скорость");
            Add(d, "校验", "Parity", "パリティ", "패리티", "Паритет");
            Add(d, "停止位", "Stop bits", "停止ビット", "정지 비트", "Стоп-биты");
            Add(d, "数据位", "Data bits", "データビット", "데이터 비트", "Биты данных");
            Add(d, "超时ms", "Timeout ms", "タイムアウトms", "제한시간ms", "Таймаут мс");
            Add(d, "刷新串口列表", "Refresh ports", "ポート更新", "포트 새로고침", "Обновить порты");
            Add(d, "启动从机", "Start slave", "スレーブ開始", "슬레이브 시작", "Запустить Slave");
            Add(d, "停止从机", "Stop slave", "スレーブ停止", "슬레이브 중지", "Остановить Slave");
            Add(d, "连接从机", "Connect", "接続", "연결", "Подключить");
            Add(d, "断开连接", "Disconnect", "切断", "연결 해제", "Отключить");
            Add(d, "读取一次", "Read once", "単発読取", "한 번 읽기", "Прочитать");
            Add(d, "自动轮询", "Poll", "自動読取", "자동 폴링", "Опрос");
            Add(d, "停止轮询", "Stop poll", "読取停止", "폴링 중지", "Стоп опроса");
            Add(d, "写入…", "Write…", "書込…", "쓰기…", "Запись…");
            Add(d, "轮询间隔ms", "Poll ms", "読取周期ms", "폴링 주기ms", "Период мс");
            Add(d, "数据区设置", "Data areas", "データ領域設定", "데이터 영역 설정", "Области данных");
            Add(d, "地址从 0 开始；修改后应用定义。", "Addresses start at 0; apply changes.", "アドレスは0開始。変更後に適用。", "주소는 0부터 시작. 변경 후 적용하세요.", "Адреса с 0. Примените изменения.");
            Add(d, "收起设置", "Hide settings", "設定を隠す", "설정 접기", "Скрыть настройки");
            Add(d, "展开设置", "Show settings", "設定を表示", "설정 펼치기", "Показать настройки");
            Add(d, "数据显示", "Data view", "データ表示", "데이터 표시", "Вид данных");
            Add(d, "列表", "List", "一覧", "목록", "Список");
            Add(d, "平铺窗格", "Tiles", "タイル", "타일", "Плитки");
            Add(d, "选项卡", "Tabs", "タブ", "탭", "Вкладки");
            Add(d, "独立浮窗", "Windows", "別ウィンドウ", "독립 창", "Окна");
            Add(d, "逐项卡片", "Cards", "カード", "카드", "Карточки");
            Add(d, "重新排列浮窗", "Arrange windows", "ウィンドウ整列", "창 다시 정렬", "Упорядочить окна");
            Add(d, "暂停显示", "Pause view", "表示停止", "표시 일시정지", "Пауза вывода");
            Add(d, "清空", "Clear", "消去", "지우기", "Очистить");
            Add(d, "导出日志", "Export log", "ログ出力", "로그 내보내기", "Экспорт журнала");
            Add(d, "通信日志", "Traffic log", "通信ログ", "통신 로그", "Журнал обмена");
            Add(d, "自定义发送", "Raw send", "任意データ送信", "사용자 정의 전송", "Своя посылка");
            Add(d, "语言", "Language", "言語", "언어", "Язык");
            Add(d, "主题", "Theme", "テーマ", "테마", "Тема");
            Add(d, "系统设置", "System settings", "システム設定", "시스템 설정", "Системные настройки");
            Add(d, "项目地址", "Project", "プロジェクト", "프로젝트", "Проект");
            Add(d, "GitHub 项目地址", "GitHub project", "GitHub プロジェクト", "GitHub 프로젝트", "Проект GitHub");
            Add(d, "无法打开项目地址：", "Cannot open project address: ", "プロジェクトのURLを開けません：", "프로젝트 주소를 열 수 없습니다: ", "Не удалось открыть адрес проекта: ");
            Add(d, "明亮模式", "Light mode", "ライトモード", "라이트 모드", "Светлый режим");
            Add(d, "暗黑模式", "Dark mode", "ダークモード", "다크 모드", "Тёмный режим");
            Add(d, "白天", "Light", "ライト", "밝게", "Светлая");
            Add(d, "黑夜", "Dark", "ダーク", "어둡게", "Тёмная");
            Add(d, "线圈", "Coils", "コイル", "코일", "Катушки");
            Add(d, "离散输入", "Discrete inputs", "ディスクリート入力", "이산 입력", "Дискретные входы");
            Add(d, "保持寄存器", "Holding registers", "保持レジスタ", "보유 레지스터", "Регистры хранения");
            Add(d, "输入寄存器", "Input registers", "入力レジスタ", "입력 레지스터", "Входные регистры");
            Add(d, "数据区 / 功能码", "Area / Function", "領域 / 機能コード", "영역 / 기능 코드", "Область / Функция");
            Add(d, "协议地址", "Address", "プロトコルアドレス", "프로토콜 주소", "Адрес");
            Add(d, "参考编号", "Reference", "参照番号", "참조 번호", "Номер");
            Add(d, "数值 · 双击编辑", "Value · double-click", "値 · ダブルクリック編集", "값 · 두 번 클릭하여 편집", "Значение · двойной щелчок");
            Add(d, "HEX 原始值", "Raw HEX", "生のHEX値", "원시 HEX 값", "Исходный HEX");
            Add(d, "解析值", "Parsed value", "変換値", "변환 값", "Преобразованное");
            Add(d, "名称 · 可编辑", "Name · editable", "名前 · 編集可", "이름 · 편집 가능", "Имя · редактируется");
            Add(d, "单位 · 可编辑", "Unit · editable", "単位 · 編集可", "단위 · 편집 가능", "Ед. · редактируется");
            Add(d, "本地名称，最多64个字符；留空使用数据区默认标题。", "Local name, up to 64 characters. Leave blank for the area title.", "ローカル名は64文字以内。空欄では領域の既定名を表示。", "로컬 이름은 최대 64자. 비워두면 기본 영역 이름을 표시합니다.", "Локальное имя до 64 символов. Пустое поле использует имя области.");
            Add(d, "本地显示单位，最多32个字符；留空隐藏单位，不写入设备。", "Display unit, up to 32 characters. Blank hides it. Never sent to the device.", "表示単位は32文字以内。空欄で非表示。機器には書き込みません。", "표시 단위는 최대 32자. 비워두면 숨깁니다. 장치에 쓰지 않습니다.", "Единица до 32 символов. Пустое поле скрывает её. В устройство не записывается.");
            Add(d, "起始地址：", "Start:", "開始アドレス：", "시작 주소:", "Начало:");
            Add(d, "数量：", "Count:", "点数：", "개수:", "Количество:");
            Add(d, "应用数据区定义", "Apply area", "領域定義を適用", "영역 정의 적용", "Применить область");
            Add(d, "原始值格式", "Raw format", "生値の形式", "원시 값 형식", "Формат значения");
            Add(d, "位值 0 / 1", "Bit 0 / 1", "ビット 0 / 1", "비트 0 / 1", "Бит 0 / 1");
            Add(d, "无符号十进制", "Unsigned decimal", "符号なし10進数", "부호 없는 10진수", "Без знака, DEC");
            Add(d, "有符号十进制", "Signed decimal", "符号付き10進数", "부호 있는 10진수", "Со знаком, DEC");
            Add(d, "十六进制", "Hexadecimal", "16進数", "16진수", "HEX");
            Add(d, "批量初值", "Fill value", "一括初期値", "일괄 초기값", "Нач. значение");
            Add(d, "常数填充", "Fill constant", "定数で埋める", "상수 채우기", "Заполнить");
            Add(d, "递增填充", "Fill increment", "連番で埋める", "증가값 채우기", "По возрастанию");
            Add(d, "跳转地址：", "Go to:", "移動先：", "이동 주소:", "Перейти:");
            Add(d, "定位", "Locate", "移動", "이동", "Найти");
            Add(d, "整区默认解析设置…", "Area parse defaults…", "領域の既定変換…", "영역 기본 변환 설정…", "Правило области…");
            Add(d, "单项解析设置…", "Item parse rule…", "項目の変換設定…", "개별 변환 설정…", "Правило элемента…");
            Add(d, "单项解析设置", "Item rule", "項目の変換", "개별 변환", "Правило элемента");
            Add(d, "双击解析值列修改公式", "Double-click parsed value to edit formula", "変換値をダブルクリックして式を編集", "변환 값을 두 번 클릭하여 수식 편집", "Двойной щелчок по значению для формулы");
            Add(d, "整区默认解析（不覆盖单项规则）", "Area default (keeps item rules)", "領域の既定設定（個別設定を保持）", "영역 기본값 (개별 규칙 유지)", "Правило области (сохраняет частные)");
            Add(d, "数据类型", "Data type", "データ型", "데이터 형식", "Тип данных");
            Add(d, "字节顺序", "Byte order", "バイト順", "바이트 순서", "Порядок байтов");
            Add(d, "运算符", "Operator", "演算子", "연산자", "Оператор");
            Add(d, "运算参数", "Operand", "演算パラメータ", "연산 인수", "Операнд");
            Add(d, "运算后偏移", "Offset", "演算後オフセット", "연산 후 오프셋", "Смещение");
            Add(d, "小数位数", "Decimals", "小数桁数", "소수 자릿수", "Десятичные знаки");
            Add(d, "显示单位", "Display unit", "表示単位", "표시 단위", "Единица");
            Add(d, "显示值 = (原始值 运算符 参数) + 偏移\r\nABCD 大端 / DCBA 小端 / BADC 字节交换 / CDAB 字交换", "Display = (raw operator operand) + offset\r\nABCD big endian / DCBA little endian / BADC byte swap / CDAB word swap", "表示値 = (生値 演算子 パラメータ) + オフセット\r\nABCD ビッグ / DCBA リトル / BADC バイト交換 / CDAB ワード交換", "표시값 = (원시값 연산자 인수) + 오프셋\r\nABCD 빅엔디언 / DCBA 리틀엔디언 / BADC 바이트 교환 / CDAB 워드 교환", "Значение = (исходное оператор операнд) + смещение\r\nABCD старшие / DCBA младшие / BADC обмен байтов / CDAB обмен слов");
            Add(d, "应用规则", "Apply rule", "規則を適用", "규칙 적용", "Применить");
            Add(d, "取消", "Cancel", "キャンセル", "취소", "Отмена");
            Add(d, "名称：", "Name:", "名前：", "이름:", "Имя:");
            Add(d, "单位：", "Unit:", "単位：", "단위:", "Ед.:");
            Add(d, "数值：", "Value:", "値：", "값:", "Значение:");
            Add(d, "保存名称", "Save label", "名前を保存", "이름 저장", "Сохранить имя");
            Add(d, "名称和单位已保存", "Name and unit saved", "名前と単位を保存しました", "이름과 단위를 저장했습니다", "Имя и единица сохранены");
            Add(d, "写入数值", "Write value", "値を書込", "값 쓰기", "Записать значение");
            Add(d, "使用整区默认", "Use area default", "領域の既定値", "영역 기본값 사용", "Правило области");
            Add(d, "拖拽此处调整卡片顺序", "Drag to reorder cards", "ドラッグしてカードを並べ替え", "드래그하여 카드 순서 변경", "Перетащите для смены порядка");
            Add(d, "恢复默认排序", "Reset order", "既定の順序に戻す", "기본 순서 복원", "Исходный порядок");
            Add(d, "收起", "Collapse", "閉じる", "접기", "Свернуть");
            Add(d, "编辑名称，点击开关修改状态", "Edit name; use switch for state", "名前を編集、スイッチで状態変更", "이름 편집, 스위치로 상태 변경", "Имя; состояние переключателем");
            Add(d, "编辑名称、单位和数值", "Edit name, unit and value", "名前・単位・値を編集", "이름, 단위 및 값 편집", "Имя, единица и значение");
            Add(d, "编辑名称 · 线圈开关写设备，离散输入只读", "Edit name · coil switch writes; inputs read-only", "名前を編集 · コイルは書込、入力は読取専用", "이름 편집 · 코일 쓰기, 이산 입력 읽기 전용", "Имя · катушки записываются, входы только чтение");
            Add(d, "点击卡片开关模拟 0 / 1 状态，修改后立即响应主站读取。", "Switch simulates 0 / 1; reads use the new state immediately.", "スイッチで0 / 1を模擬。変更は直ちに読取に反映。", "스위치로 0 / 1 상태 모의. 변경 즉시 읽기에 반영됩니다.", "Переключатель имитирует 0 / 1; новое состояние сразу доступно для чтения.");
            Add(d, "Enter确认；Esc或点击别处取消。按当前解析规则写入。", "Enter confirms; Esc or click elsewhere cancels. Uses the current parse rule.", "Enterで確定、Escまたは外側をクリックで取消。現在の変換規則で書込。", "Enter 확인, Esc 또는 다른 곳 클릭 취소. 현재 변환 규칙으로 씁니다.", "Enter — подтвердить; Esc или щелчок снаружи — отменить. Текущее правило преобразования.");
            Add(d, "Enter确认；Esc取消。", "Enter confirms; Esc cancels.", "Enterで確定、Escで取消。", "Enter 확인, Esc 취소.", "Enter — подтвердить; Esc — отменить.");
            Add(d, "双击数值快速编辑，Enter确认。", "Double-click value to edit; Enter confirms.", "値をダブルクリックで編集、Enterで確定。", "값을 두 번 클릭하여 편집, Enter 확인.", "Двойной щелчок — правка; Enter — подтвердить.");
            Add(d, "点击开关模拟状态", "Switch to simulate state", "スイッチで状態を模擬", "스위치로 상태 모의", "Переключатель имитирует состояние");
            Add(d, "开关发送05写线圈，确认后读回刷新。", "Switch sends function 05; reads back after confirmation.", "スイッチで05書込、確認後に再読取。", "스위치는 05 쓰기 전송, 확인 후 다시 읽습니다.", "Переключатель отправляет 05; после подтверждения — чтение.");
            Add(d, "离散输入只读；卡片显示设备上次成功读取的状态。", "Discrete inputs are read-only; cards show the last successful read.", "ディスクリート入力は読取専用。最後に読取成功した状態を表示。", "이산 입력은 읽기 전용. 마지막 읽기 성공 상태를 표시합니다.", "Дискретные входы только для чтения; показано последнее успешное чтение.");
            Add(d, "当前只读或未连接。", "Read-only or disconnected.", "読取専用または未接続。", "읽기 전용 또는 연결되지 않음.", "Только чтение или нет подключения.");
            Add(d, "无法解析", "Parse error", "変換できません", "변환 오류", "Ошибка преобразования");
            Add(d, "已写入原始寄存器", "Raw registers updated", "生レジスタに書込済み", "원시 레지스터에 썼습니다", "Исходные регистры обновлены");
            Add(d, "目标", "Target", "送信先", "대상", "Цель");
            Add(d, "发送", "Send", "送信", "전송", "Отправить");
            Add(d, "循环发送", "Repeat", "繰返し", "반복 전송", "Повторять");
            Add(d, "低字节在前", "Low byte first", "下位バイト先頭", "하위 바이트 먼저", "Младший байт первым");
            Add(d, "高字节在前", "High byte first", "上位バイト先頭", "상위 바이트 먼저", "Старший байт первым");
            Add(d, "无换行", "No newline", "改行なし", "줄바꿈 없음", "Без перевода строки");
            Add(d, "无校验", "No checksum", "チェックサムなし", "체크섬 없음", "Без контрольной суммы");
            Add(d, "原始发送目标", "Raw send target", "任意データ送信先", "원시 전송 대상", "Цель отправки");
            Add(d, "原始发送附加校验", "Append checksum", "チェックサムを付加", "체크섬 추가", "Добавить контрольную сумму");
            Add(d, "校验字节序", "Checksum byte order", "チェックサムのバイト順", "체크섬 바이트 순서", "Порядок байтов суммы");
            Add(d, "发送末尾换行", "Append newline", "末尾改行", "끝에 줄바꿈 추가", "Добавить перевод строки");
            Add(d, "自定义原始报文输入", "Raw message input", "任意電文入力", "원시 메시지 입력", "Ввод посылки");
            Add(d, "原始发送状态及字节预览", "Raw send status and preview", "送信状態とバイトプレビュー", "원시 전송 상태 및 미리보기", "Статус и просмотр посылки");
            Add(d, "输入原始字节→校验→换行，不再自动包Modbus。标准 Modbus TCP 通常选无校验。", "Raw bytes → checksum → newline. No Modbus wrapping. Standard Modbus TCP normally uses no checksum.", "生バイト→チェックサム→改行。Modbus自動付加なし。標準Modbus TCPは通常チェックサムなし。", "원시 바이트 → 체크섬 → 줄바꿈. Modbus 포장 없음. 표준 Modbus TCP는 보통 체크섬 없음.", "Байты → контрольная сумма → перевод строки. Без обёртки Modbus. Стандартный Modbus TCP обычно без суммы.");
            Add(d, "多字节校验值的附加顺序，包括CRC16、CRC32及SUM16。", "Append order for multi-byte checksums: CRC16, CRC32 and SUM16.", "CRC16、CRC32、SUM16など複数バイトの付加順。", "CRC16, CRC32, SUM16 등 다중 바이트 체크섬 추가 순서.", "Порядок добавления многобайтовой суммы: CRC16, CRC32, SUM16.");
            Add(d, "立即发送一次，再按周期发送。通道忙时跳过；连接/目标改变或发送失败时停止，重新打开软件不会自动发送。", "Sends once, then repeats. Skips busy cycles; stops on connection/target change or failure. Never starts automatically on launch.", "直ちに1回送信後、周期送信。通信中はスキップ。接続・送信先の変更や失敗で停止。起動時は自動送信しません。", "즉시 한 번 전송 후 반복. 채널 사용 중 건너뛰며 연결/대상 변경 또는 실패 시 중지. 재실행 시 자동 전송하지 않습니다.", "Сначала отправляет сразу, затем по периоду. Занятые циклы пропускает. Изменение связи/цели или ошибка останавливают. При запуске не включается.");
            Add(d, "循环周期20～600000ms；实际发送受通道及响应时间限制，不积压定时任务。", "Period: 20–600000 ms. Channel and response time limit sends; missed ticks are not queued.", "周期20～600000ms。通信と応答時間が優先。未処理の周期送信は蓄積しません。", "주기 20~600000ms. 채널 및 응답 시간에 따라 전송하며 작업을 누적하지 않습니다.", "Период 20–600000 мс. Ограничен каналом и ответом; пропущенные такты не копятся.");
            Add(d, "仅向当前选中的目标发送；多个目标必须手动选择。", "Sends only to the selected target; choose manually when multiple targets exist.", "選択中の送信先だけに送信。複数ある場合は手動選択。", "선택한 대상에만 전송. 대상이 여럿이면 직접 선택하세요.", "Отправка только выбранной цели; при нескольких целях выберите вручную.");
            Add(d, "严格 ASCII 0～127，保留实际换行，不解释 \\r、\\n。", "Strict ASCII 0–127. Preserves real newlines; does not interpret \\r or \\n.", "ASCII 0～127のみ。実際の改行を保持。\\r・\\nは解釈しません。", "ASCII 0~127만 허용. 실제 줄바꿈 유지, \\r 및 \\n 해석 없음.", "ASCII 0–127. Реальные переводы строк сохраняются; \\r и \\n не интерпретируются.");
            Add(d, "手动发送当前编排结果；已提交不代表对方已收到。", "Sends the current bytes. Submitted does not mean received.", "現在のバイト列を送信。送信受付は受信完了を意味しません。", "현재 바이트를 전송. 제출은 수신 완료를 뜻하지 않습니다.", "Отправляет текущие байты. Приём в очередь не означает доставку.");
            Add(d, "串口 · RTU", "Serial · RTU", "シリアル · RTU", "직렬 · RTU", "COM · RTU");
            Add(d, "串口 · ASCII", "Serial · ASCII", "シリアル · ASCII", "직렬 · ASCII", "COM · ASCII");
            Add(d, "串口 · MBAP 扩展", "Serial · MBAP extension", "シリアル · MBAP拡張", "직렬 · MBAP 확장", "COM · расширение MBAP");
            Add(d, "网口 · TCP/IP", "Network · TCP/IP", "ネットワーク · TCP/IP", "네트워크 · TCP/IP", "Сеть · TCP/IP");
            Add(d, "网口 · UDP/IP", "Network · UDP/IP", "ネットワーク · UDP/IP", "네트워크 · UDP/IP", "Сеть · UDP/IP");
            Add(d, "网口 · RTU over TCP", "Network · RTU over TCP", "ネットワーク · RTU over TCP", "네트워크 · RTU over TCP", "Сеть · RTU over TCP");
            Add(d, "网口 · RTU over UDP", "Network · RTU over UDP", "ネットワーク · RTU over UDP", "네트워크 · RTU over UDP", "Сеть · RTU over UDP");
            Add(d, "写入设备 · 确认后读回", "Write device · read back after confirmation", "機器へ書込 · 確認後再読取", "장치 쓰기 · 확인 후 다시 읽기", "Запись · чтение после подтверждения");
            Add(d, "05 · 写单个线圈", "05 · Write single coil", "05 · 単一コイル書込", "05 · 단일 코일 쓰기", "05 · Запись катушки");
            Add(d, "06 · 写单个保持寄存器", "06 · Write single register", "06 · 単一保持レジスタ書込", "06 · 단일 보유 레지스터 쓰기", "06 · Запись регистра");
            Add(d, "0F · 写多个线圈", "0F · Write multiple coils", "0F · 複数コイル書込", "0F · 여러 코일 쓰기", "0F · Запись катушек");
            Add(d, "10 · 写多个保持寄存器", "10 · Write multiple registers", "10 · 複数保持レジスタ書込", "10 · 여러 보유 레지스터 쓰기", "10 · Запись регистров");
            Add(d, "无符号 / 0x十六进制", "Unsigned / 0x hex", "符号なし / 0x16進数", "부호 없음 / 0x16진수", "Без знака / 0x HEX");
            Add(d, "有符号16位 / 0x十六进制", "Signed 16-bit / 0x hex", "符号付き16bit / 0x16進数", "부호 있는 16비트 / 0x16진수", "Со знаком 16 бит / 0x HEX");
            Add(d, "功能码", "Function", "機能コード", "기능 코드", "Функция");
            Add(d, "输入格式", "Input format", "入力形式", "입력 형식", "Формат ввода");
            Add(d, "多个数值用空格、逗号或换行分隔；线圈只能填0/1。\r\n写入范围须位于已配置的数据区内；浮点/工程值可直接在卡片写入。", "Separate values with spaces, commas or newlines; coils accept 0/1 only.\r\nThe range must be configured. Write floating/engineering values directly on cards.", "値は空白・カンマ・改行で区切る。コイルは0/1のみ。\r\n設定済みの領域内に書込。浮動小数・工学値はカードから書込可能。", "공백, 쉼표 또는 줄바꿈으로 구분. 코일은 0/1만 허용.\r\n설정된 영역 안에 써야 합니다. 실수/공학 값은 카드에서 쓰세요.", "Разделяйте пробелами, запятыми или переводами строк; катушки только 0/1.\r\nДиапазон должен быть настроен. Вещественные значения пишутся на карточках.");
            Add(d, "发送写入", "Send write", "書込送信", "쓰기 전송", "Записать");
            Add(d, "复制选中日志   Ctrl+C", "Copy selection   Ctrl+C", "選択ログをコピー   Ctrl+C", "선택 로그 복사   Ctrl+C", "Копировать выделенное   Ctrl+C");
            Add(d, "复制全部日志", "Copy all logs", "全ログをコピー", "모든 로그 복사", "Копировать весь журнал");
            Add(d, "全选   Ctrl+A", "Select all   Ctrl+A", "すべて選択   Ctrl+A", "모두 선택   Ctrl+A", "Выделить всё   Ctrl+A");
            Add(d, "已停止", "Stopped", "停止", "중지됨", "Остановлено");
            Add(d, "运行中", "Running", "実行中", "실행 중", "Работает");
            Add(d, "未连接", "Disconnected", "未接続", "연결 안 됨", "Нет подключения");
            Add(d, "已连接", "Connected", "接続済み", "연결됨", "Подключено");
            Add(d, "显示缓存", "cached values", "キャッシュ表示", "캐시 표시", "кэшированные значения");
            Add(d, "数据保留", "data retained", "データ保持", "데이터 유지", "данные сохранены");
            Add(d, "请求 ", "Requests ", "要求 ", "요청 ", "Запросы ");
            Add(d, "错误 ", "Errors ", "エラー ", "오류 ", "Ошибки ");
            Add(d, "字节", "bytes", "バイト", "바이트", "байт");
            Add(d, "地址 ", "Address ", "アドレス ", "주소 ", "Адрес ");
            Add(d, "地址=", "Address=", "アドレス=", "주소=", "Адрес=");
            Add(d, "原始值 ", "raw ", "生値 ", "원시값 ", "исходное ");
            Add(d, "显示值", "Display", "表示値", "표시값", "Значение");
            Add(d, "整区默认", "Area default", "領域の既定", "영역 기본값", "Правило области");
            Add(d, "单项解析", "Item rule", "項目の変換", "개별 변환", "Правило элемента");
            Add(d, "本地模拟", "local simulation", "ローカル模擬", "로컬 모의", "локальная имитация");
            Add(d, "读 / ", "read / ", "読取 / ", "읽기 / ", "чтение / ");
            Add(d, "读 · ", "read · ", "読取 · ", "읽기 · ", "чтение · ");
            Add(d, " 写", " write", " 書込", " 쓰기", " запись");
            Add(d, "原始发送", "Raw send", "生データ送信", "원시 전송", "Отправка байтов");
            Add(d, "未启用数据区", "No active areas", "有効な領域なし", "활성 영역 없음", "Нет активных областей");
            Add(d, "未启用（数量为 0，无可用地址）", "Disabled (count 0; no addresses)", "無効（点数0、有効なアドレスなし）", "비활성 (개수 0, 주소 없음)", "Отключено (0 элементов, нет адресов)");
            Add(d, "无启用数据区；请在设置中将所需数据区数量设为大于 0。", "No active data areas. Set the required area count above 0.", "有効な領域がありません。必要な領域の点数を0より大きく設定してください。", "활성 영역이 없습니다. 필요한 영역의 개수를 0보다 크게 설정하세요.", "Нет активных областей. Задайте количество больше 0.");
            Add(d, "上次值", "last value", "前回値", "이전 값", "прошлое значение");
            Add(d, "已更新", "updated", "更新済み", "갱신됨", "обновлено");
            Add(d, "未更新", "not updated", "未更新", "갱신 안 됨", "не обновлено");
            Add(d, "缓存", "cache", "キャッシュ", "캐시", "кэш");
            Add(d, "未读取", "Not read", "未読取", "읽지 않음", "Не прочитано");
            Add(d, "本地缓存", "local cache", "ローカルキャッシュ", "로컬 캐시", "локальный кэш");
            Add(d, "定义已改变", "definition changed", "領域定義変更", "정의 변경됨", "определение изменено");
            Add(d, "请先连接从机。", "Connect to a slave first.", "先にスレーブへ接続してください。", "먼저 슬레이브에 연결하세요.", "Сначала подключитесь к Slave.");
            Add(d, "请先断开连接。", "Disconnect first.", "先に切断してください。", "먼저 연결을 해제하세요.", "Сначала отключитесь.");
            Add(d, "请先应用数据区定义。", "Apply the area definition first.", "先に領域定義を適用してください。", "먼저 영역 정의를 적용하세요.", "Сначала примените область.");
            Add(d, "写入已排队；确认后读回刷新。", "Write queued; reads back after confirmation.", "書込をキューに追加。確認後に再読取。", "쓰기를 대기열에 추가. 확인 후 다시 읽습니다.", "Запись в очереди; затем подтверждение и чтение.");
            Add(d, "写入已排队，确认后读回刷新", "Write queued; reads back after confirmation", "書込待機、確認後に再読取", "쓰기 대기 중, 확인 후 다시 읽기", "Запись в очереди; затем чтение");
            Add(d, "无效数值；Esc 取消", "Invalid value; Esc cancels", "無効な値。Escで取消", "잘못된 값, Esc 취소", "Неверное значение; Esc — отмена");
            Add(d, "单个写入只能填写一个数值。", "Single write requires exactly one value.", "単一書込には値を1つだけ入力してください。", "단일 쓰기는 값 하나만 입력하세요.", "Для одиночной записи нужно одно значение.");
            Add(d, "起始地址+写入数量超过65536。", "Start address plus count exceeds 65536.", "開始アドレス＋点数が65536を超えています。", "시작 주소 + 개수가 65536을 초과합니다.", "Адрес плюс количество превышает 65536.");
            Add(d, "起始地址 + 数量不得超过 65536。", "Start address plus count must not exceed 65536.", "開始アドレス＋点数は65536以下にしてください。", "시작 주소 + 개수는 65536 이하여야 합니다.", "Адрес плюс количество не должны превышать 65536.");
            Add(d, "请填写1～65536个数值。", "Enter 1–65536 values.", "1～65536個の値を入力してください。", "값 1~65536개를 입력하세요.", "Введите от 1 до 65536 значений.");
            Add(d, "运算参数和偏移必须是有效数值。", "Operand and offset must be valid numbers.", "演算パラメータとオフセットには数値を入力してください。", "연산 인수와 오프셋은 유효한 숫자여야 합니다.", "Операнд и смещение должны быть числами.");
            Add(d, "新范围外的数据会被移除，重叠地址值保留。是否继续？", "Data outside the new range will be removed; overlapping values remain. Continue?", "新しい範囲外のデータを削除し、重複する値は保持します。続行しますか？", "새 범위 밖 데이터는 삭제하고 겹치는 값은 유지합니다. 계속할까요?", "Вне нового диапазона данные удалятся, общие адреса сохранятся. Продолжить?");
            Add(d, "当前区未启用，没有可定位的地址。", "This area is disabled; no address to locate.", "領域が無効なため、移動可能なアドレスがありません。", "영역이 비활성 상태이며 이동할 주소가 없습니다.", "Область отключена, доступных адресов нет.");
            Add(d, "地址不在当前区间内。", "Address is outside this area.", "アドレスが現在の領域外です。", "주소가 현재 영역 밖입니다.", "Адрес вне текущей области.");
            Add(d, "应用定义", "Apply area", "領域定義を適用", "정의 적용", "Применить область");
            Add(d, "定义无效", "Invalid definition", "領域定義が無効", "잘못된 정의", "Неверная область");
            Add(d, "批量填充", "Fill area", "一括入力", "일괄 채우기", "Заполнение");
            Add(d, "解析规则无效", "Invalid parse rule", "変換規則が無効", "잘못된 변환 규칙", "Неверное правило");
            Add(d, "写入参数", "Write parameters", "書込パラメータ", "쓰기 매개변수", "Параметры записи");
            Add(d, "连接参数", "Connection parameters", "接続パラメータ", "연결 매개변수", "Параметры связи");
            Add(d, "保存失败", "Save failed", "保存失敗", "저장 실패", "Ошибка сохранения");
            Add(d, "加载失败，旧配置保留", "Load failed; previous settings retained", "読込失敗、以前の設定を保持", "불러오기 실패, 이전 설정 유지", "Ошибка загрузки; старые настройки сохранены");
            Add(d, "导出失败", "Export failed", "出力失敗", "내보내기 실패", "Ошибка экспорта");
            Add(d, "无法启动", "Cannot start", "起動できません", "시작할 수 없음", "Невозможно запустить");
            Add(d, "退出", "Exit", "終了", "종료", "Выход");
            Add(d, "请完成编辑或按 Esc 取消无效的单元格编辑。", "Finish editing or press Esc to cancel an invalid cell edit.", "編集を完了するか、Escで無効なセル編集を取り消してください。", "편집을 완료하거나 Esc로 잘못된 셀 편집을 취소하세요.", "Завершите правку или отмените её клавишей Esc.");
            Add(d, "从机正在运行，退出将断开连接。是否退出？", "The slave is running. Exit and disconnect?", "スレーブ実行中です。切断して終了しますか？", "슬레이브 실행 중입니다. 연결을 해제하고 종료할까요?", "Slave работает. Выйти и отключиться?");
            Add(d, "请先停止从机。", "Stop the slave first.", "先にスレーブを停止してください。", "먼저 슬레이브를 중지하세요.", "Сначала остановите Slave.");
            Add(d, "输入寄存器只读。", "Input registers are read-only.", "入力レジスタは読取専用です。", "입력 레지스터는 읽기 전용입니다.", "Входные регистры только для чтения.");
            Add(d, "请输入有效解析数值。", "Enter a valid parsed value.", "有効な変換値を入力してください。", "유효한 변환 값을 입력하세요.", "Введите допустимое значение.");
            Add(d, "未读取 · 配置中的数据是缓存快照，连接后读取；写入确认后读回刷新", "Not read · settings contain cached values; connect to read. Writes are read back after confirmation.", "未読取 · 設定値はキャッシュです。接続後に読取。書込確認後に再読取。", "읽지 않음 · 설정 데이터는 캐시입니다. 연결 후 읽기. 쓰기 확인 후 다시 읽습니다.", "Не прочитано · в настройках кэш. Подключитесь для чтения. После записи — чтение.");
            Add(d, "本地编辑立即响应 · Enter 提交", "Local edits apply immediately · Enter confirms", "ローカル編集は即時反映 · Enterで確定", "로컬 편집 즉시 반영 · Enter 확인", "Правки применяются сразу · Enter подтверждает");
            Add(d, "四区同时响应 · 双击或 Enter 提交 · HEX 带 0x · 解析规则可按地址设置 · 日志分隔线可拖动", "Four areas active · Double-click or Enter · HEX uses 0x · Per-address rules · Drag log divider", "4領域同時応答 · ダブルクリック/Enter · HEXは0x · アドレス別規則 · ログ境界をドラッグ", "4영역 동시 응답 · 두 번 클릭/Enter · HEX는 0x · 주소별 규칙 · 로그 구분선 드래그", "4 области · Щелчок/Enter · HEX с 0x · Правила по адресам · Граница журнала перемещается");
            Add(d, "各数据区已在独立窗口显示\r\n\r\n可拖动、缩放，放到其他显示器。\r\n关闭某个浮窗仅隐藏视图，不停止从机。\r\n点击“重新排列浮窗”可再次显示全部。", "Data areas are shown in separate windows.\r\n\r\nMove or resize them, including on other monitors.\r\nClosing a window only hides its view.\r\nUse Arrange windows to show them again.", "各領域を別ウィンドウに表示しています。\r\n\r\n移動・サイズ変更・別画面への配置が可能です。\r\n閉じても表示を隠すだけです。\r\n「ウィンドウ整列」で再表示します。", "영역을 독립 창에 표시합니다.\r\n\r\n다른 모니터로 이동하거나 크기를 조절하세요.\r\n창을 닫으면 보기만 숨깁니다.\r\n창 다시 정렬로 모두 표시하세요.", "Области показаны в отдельных окнах.\r\n\r\nПеремещайте и меняйте размер, включая другие мониторы.\r\nЗакрытие лишь скрывает вид.\r\nУпорядочить окна покажет их снова.");
            Add(d, "已启用的数据区在独立窗口显示", "Active areas are in separate windows", "有効な領域を別ウィンドウに表示", "활성 영역을 독립 창에 표시", "Активные области в отдельных окнах");
            Add(d, "可拖动、缩放，放到其他显示器。", "Move and resize, including on other monitors.", "移動・サイズ変更・別画面への配置が可能です。", "다른 모니터로 이동 및 크기 조절 가능.", "Перемещайте и меняйте размер, включая другие мониторы.");
            Add(d, "关闭某个浮窗仅隐藏视图，不停止从机。", "Closing a window only hides its view; the slave keeps running.", "閉じると表示のみを隠し、スレーブは停止しません。", "창을 닫아도 보기만 숨기며 슬레이브는 계속 실행됩니다.", "Закрытие скрывает вид; Slave продолжает работу.");
            Add(d, "点击“重新排列浮窗”可再次显示全部。", "Use Arrange windows to show all windows again.", "「ウィンドウ整列」で再表示します。", "창 다시 정렬로 모두 표시하세요.", "Упорядочить окна покажет все окна снова.");
            Add(d, "界面语言", "UI language", "表示言語", "화면 언어", "Язык интерфейса");
            Add(d, "界面主题", "UI theme", "表示テーマ", "화면 테마", "Тема интерфейса");
            Add(d, "监听IP", "Listen IP", "待受IP", "수신 IP", "IP прослушивания");
            Add(d, "远端从机IP", "Remote slave IP", "接続先IP", "원격 슬레이브 IP", "IP удалённого Slave");
            Add(d, "四区响应 · Enter 提交", "Four areas · Enter confirms", "4領域応答 · Enterで確定", "4영역 응답 · Enter 확인", "4 области · Enter подтверждает");
            Add(d, "四区同时响应 · 按 Enter 提交 · HEX 加 0x · 设置区可滚动，日志分隔条可拖动", "Four areas · Enter confirms · HEX uses 0x · Scroll settings; drag the log divider", "4領域同時応答 · Enterで確定 · HEXは0x · 設定スクロール・ログ境界ドラッグ可", "4영역 동시 응답 · Enter 확인 · HEX는 0x · 설정 스크롤, 로그 경계 드래그", "4 области · Enter · HEX с 0x · Прокрутка настроек и граница журнала");
            Add(d, "目标刷新失败：", "Target refresh failed: ", "送信先の更新失敗：", "대상 새로고침 실패: ", "Ошибка обновления цели: ");
            Add(d, "原目标已失效，请重新选择发送目标", "Previous target is unavailable; choose a target again", "以前の送信先は無効です。再選択してください", "이전 대상을 사용할 수 없습니다. 다시 선택하세요", "Прежняя цель недоступна; выберите заново");
            Add(d, "请选择发送目标", "Select a send target", "送信先を選択してください", "전송 대상을 선택하세요", "Выберите цель отправки");
            Add(d, "循环发送已停止：目标不可用。", "Repeat stopped: target unavailable.", "周期送信停止：送信先が無効です。", "반복 전송 중지: 대상 사용 불가.", "Повтор остановлен: цель недоступна.");
            Add(d, "循环发送已停止：连接或目标已改变，请重新选择。", "Repeat stopped: connection or target changed; select again.", "周期送信停止：接続または送信先が変更されました。再選択してください。", "반복 전송 중지: 연결 또는 대상이 변경되었습니다. 다시 선택하세요.", "Повтор остановлен: связь или цель изменились. Выберите заново.");
            Add(d, "暂无可发送目标，请先建立连接。", "No send target available. Connect first.", "送信先がありません。先に接続してください。", "전송 대상이 없습니다. 먼저 연결하세요.", "Нет цели отправки. Сначала подключитесь.");
            Add(d, "循环发送已停止：没有可用目标。", "Repeat stopped: no available target.", "周期送信停止：有効な送信先がありません。", "반복 전송 중지: 사용 가능한 대상 없음.", "Повтор остановлен: нет доступной цели.");
            Add(d, "发送接口尚未就绪。", "Send interface is not ready.", "送信インターフェースの準備ができていません。", "전송 인터페이스가 준비되지 않았습니다.", "Интерфейс отправки не готов.");
            Add(d, "循环发送已停止：发送接口未就绪。", "Repeat stopped: send interface not ready.", "周期送信停止：送信の準備ができていません。", "반복 전송 중지: 전송 인터페이스 준비 안 됨.", "Повтор остановлен: интерфейс не готов.");
            Add(d, "已提交发送", "Send submitted", "送信受付済み", "전송 제출됨", "Посылка принята");
            Add(d, "发送未提交，请检查连接或通信日志。", "Send was not submitted. Check connection or traffic log.", "送信が受け付けられませんでした。接続または通信ログを確認してください。", "전송되지 않았습니다. 연결 또는 통신 로그를 확인하세요.", "Посылка не принята. Проверьте связь или журнал.");
            Add(d, "循环发送已停止：发送未提交，请检查日志。", "Repeat stopped: send not submitted; check the log.", "周期送信停止：送信未受付。ログを確認してください。", "반복 전송 중지: 전송되지 않음. 로그를 확인하세요.", "Повтор остановлен: посылка не принята. Проверьте журнал.");
            Add(d, "发送失败：", "Send failed: ", "送信失敗：", "전송 실패: ", "Ошибка отправки: ");
            Add(d, "请先连接并选择有效发送目标。", "Connect and select a valid target first.", "先に接続し、有効な送信先を選択してください。", "먼저 연결하고 유효한 전송 대상을 선택하세요.", "Сначала подключитесь и выберите допустимую цель.");
            Add(d, "循环发送未启动：", "Repeat did not start: ", "周期送信を開始できません：", "반복 전송 시작 실패: ", "Повтор не запущен: ");
            Add(d, "循环发送已停止：连接已断开。", "Repeat stopped: disconnected.", "周期送信停止：接続が切断されました。", "반복 전송 중지: 연결 해제됨.", "Повтор остановлен: связь разорвана.");
            Add(d, "已停止循环发送。", "Repeat stopped.", "周期送信を停止しました。", "반복 전송을 중지했습니다.", "Повтор остановлен.");
            Add(d, "位对象请使用开关按钮。", "Use the switch for bit values.", "ビット値はスイッチで操作してください。", "비트 값은 스위치를 사용하세요.", "Для битов используйте переключатель.");
            Add(d, "原值 ", "raw ", "生値 ", "원시값 ", "исходное ");
            Add(d, "关闭某个浮窗仅隐藏视图，不停止通信。", "Closing a window only hides its view; communication continues.", "閉じても表示を隠すだけで、通信は継続します。", "창을 닫아도 보기만 숨기며 통신은 계속됩니다.", "Закрытие лишь скрывает вид, обмен продолжается.");
            Add(d, "Modbus 主机", "Modbus Master", "Modbus マスター", "Modbus 마스터", "Modbus Master");
            Add(d, "离散", "Discrete", "離散", "이산", "Дискретные");
            Add(d, "保持", "Holding", "保持", "보유", "Хранение");
            Add(d, "输入", "Input", "入力", "입력", "Вход");
            Add(d, "连接中，显示上次值", "Connecting; showing last values", "接続中、前回値を表示", "연결 중, 이전 값 표시", "Подключение; прошлые значения");
            Add(d, "已断开", "Disconnected", "切断済み", "연결 해제됨", "Отключено");
            Add(d, "原始模式", "Raw mode", "生データモード", "원시 모드", "Режим байтов");
            Add(d, "配置快照", "configuration snapshot", "設定スナップショット", "설정 스냅샷", "снимок настроек");
            Add(d, "尚未读取", "not read yet", "未読取", "아직 읽지 않음", "ещё не прочитано");
            Add(d, "整区未读取", "Area not read", "領域未読取", "영역 읽지 않음", "Область не прочитана");
            Add(d, "整区读取成功", "Area read successfully", "領域読取成功", "영역 읽기 성공", "Область прочитана");
            Add(d, "上次成功 ", "Last success ", "最終成功 ", "마지막 성공 ", "Последний успех ");
            Add(d, "尚无成功读取", "No successful read yet", "読取成功なし", "읽기 성공 기록 없음", "Успешного чтения ещё нет");
            Add(d, "读取失败", "Read failed", "読取失敗", "읽기 실패", "Ошибка чтения");
            Add(d, "局部写回读成功 地址", "Partial write read-back succeeded at ", "部分書込の再読取成功 アドレス", "부분 쓰기 재읽기 성공 주소", "Частичная запись подтверждена чтением, адрес ");
            Add(d, "整区状态不变", "whole-area state unchanged", "領域全体の状態は維持", "전체 영역 상태 유지", "статус всей области не изменён");
            Add(d, "局部写入已回读，整区仍为读取失败/上次值", "Partial write read back; whole area still shows failed/last read", "部分書込再読取済み。領域全体は読取失敗・前回値のまま", "부분 쓰기 재읽기 완료. 전체 영역은 실패/이전 값 상태 유지", "Частичная запись прочитана; область ещё с ошибкой/прошлым значением");
            Add(d, "局部写入已回读", "partial write read back", "部分書込再読取済み", "부분 쓰기 재읽기 완료", "частичная запись прочитана");
            Add(d, "空数据区", "empty area", "空の領域", "빈 영역", "пустая область");
            Add(d, "缓存值", "cached values", "キャッシュ値", "캐시 값", "кэшированные значения");
            Add(d, "连接中…", "Connecting…", "接続中…", "연결 중…", "Подключение…");
            Add(d, "轮询中", "polling", "自動読取中", "폴링 중", "опрос");
            Add(d, "通信已断开，轮询停止", "Disconnected; polling stopped", "切断され、自動読取を停止", "연결 해제, 폴링 중지", "Связь разорвана; опрос остановлен");
            Add(d, "正在断开…", "Disconnecting…", "切断中…", "연결 해제 중…", "Отключение…");
            Add(d, "尾部不足/规则无效", "Insufficient tail / invalid rule", "末尾不足 / 規則無効", "끝 데이터 부족 / 잘못된 규칙", "Недостаточно данных / неверное правило");
            Add(d, "位区不支持解析规则设置。", "Bit areas do not use parse rules.", "ビット領域では変換規則を使用しません。", "비트 영역은 변환 규칙을 사용하지 않습니다.", "Битовые области не используют правила преобразования.");
            Add(d, "单位最多32个字符；Esc 取消", "Unit: up to 32 characters; Esc cancels", "単位は32文字以内。Escで取消", "단위 최대 32자, Esc 취소", "Единица до 32 символов; Esc — отмена");
            Add(d, "名称最多64个字符；Esc 取消", "Name: up to 64 characters; Esc cancels", "名前は64文字以内。Escで取消", "이름 최대 64자, Esc 취소", "Имя до 64 символов; Esc — отмена");
            Add(d, "批量写值", "Write area", "一括書込", "일괄 값 쓰기", "Запись области");
            Add(d, "常数写入", "Write constant", "定数書込", "상수 쓰기", "Записать константу");
            Add(d, "递增写入", "Write increment", "連番書込", "증가값 쓰기", "Записать по возрастанию");
            Add(d, "批量写入", "Bulk write", "一括書込", "일괄 쓰기", "Массовая запись");
            Add(d, "此数据区只读。", "This area is read-only.", "この領域は読取専用です。", "이 영역은 읽기 전용입니다.", "Эта область только для чтения.");
            Add(d, "当前区只读。", "This area is read-only.", "現在の領域は読取専用です。", "현재 영역은 읽기 전용입니다.", "Текущая область только для чтения.");
            Add(d, "当前区未启用。", "This area is disabled.", "現在の領域は無効です。", "현재 영역이 비활성 상태입니다.", "Текущая область отключена.");
            Add(d, "写入未加入队列，请查看通信日志。", "Write not queued; check the traffic log.", "書込を受け付けられませんでした。通信ログを確認してください。", "쓰기가 대기열에 추가되지 않음. 통신 로그를 확인하세요.", "Запись не попала в очередь; проверьте журнал.");
            Add(d, "请先停止服务。", "Stop the service first.", "先にサービスを停止してください。", "먼저 서비스를 중지하세요.", "Сначала остановите службу.");
            Add(d, "请完成或取消无效编辑。", "Finish or cancel invalid edits.", "無効な編集を完了または取り消してください。", "잘못된 편집을 완료하거나 취소하세요.", "Завершите или отмените неверную правку.");
            Add(d, "请先完成编辑。", "Finish editing first.", "先に編集を完了してください。", "먼저 편집을 완료하세요.", "Сначала завершите правку.");
            Add(d, "初值无效，HEX 请加 0x 前缀。", "Invalid initial value; prefix HEX with 0x.", "初期値が無効です。HEXには0xを付けてください。", "잘못된 초기값. HEX는 0x 접두어를 사용하세요.", "Неверное начальное значение; HEX с префиксом 0x.");
            Add(d, "无效写入值：", "Invalid write value: ", "無効な書込値：", "잘못된 쓰기 값: ", "Неверное значение записи: ");
            Add(d, "未启用", "Disabled", "無効", "비활성", "Отключено");
            Add(d, "只读", "read-only", "読取専用", "읽기 전용", "только чтение");
            Add(d, "请先断开当前连接。", "Disconnect the current connection first.", "先に現在の接続を切断してください。", "먼저 현재 연결을 해제하세요.", "Сначала разорвите текущее подключение.");
            Add(d, "请完成编辑并应用数据区定义。", "Finish editing and apply the area definition.", "編集を完了し、領域定義を適用してください。", "편집을 완료하고 영역 정의를 적용하세요.", "Завершите правку и примените область.");
            Add(d, "请先完成编辑并应用数据区定义。", "Finish editing and apply the area definition first.", "先に編集を完了し、領域定義を適用してください。", "먼저 편집을 완료하고 영역 정의를 적용하세요.", "Сначала завершите правку и примените область.");
            Add(d, "从机正在运行，退出会断开连接。是否退出？", "The slave is running. Exit and disconnect?", "スレーブ実行中です。切断して終了しますか？", "슬레이브 실행 중입니다. 연결을 해제하고 종료할까요?", "Slave работает. Выйти и отключиться?");
            Add(d, "TCP/IP：MBAP + PDU，无CRC。", "TCP/IP: MBAP + PDU, no CRC.", "TCP/IP：MBAP + PDU、CRCなし。", "TCP/IP: MBAP + PDU, CRC 없음.", "TCP/IP: MBAP + PDU, без CRC.");
            Add(d, "UDP/IP：每数据报一个MBAP帧，无CRC。", "UDP/IP: one MBAP frame per datagram, no CRC.", "UDP/IP：1データグラムに1つのMBAPフレーム、CRCなし。", "UDP/IP: 데이터그램당 MBAP 프레임 하나, CRC 없음.", "UDP/IP: один кадр MBAP в дейтаграмме, без CRC.");
            Add(d, "RTU Over TCP/IP：带CRC的RTU字节流，支持拆包/粘包。", "RTU over TCP/IP: RTU byte stream with CRC; handles split/combined frames.", "RTU over TCP/IP：CRC付きRTUバイト列。分割・結合受信に対応。", "RTU over TCP/IP: CRC 포함 RTU 바이트 스트림. 분할/결합 프레임 처리.", "RTU over TCP/IP: поток RTU с CRC, обработка разделённых и объединённых кадров.");
            Add(d, "RTU Over UDP/IP：每数据报一个RTU帧，包含CRC。", "RTU over UDP/IP: one RTU frame with CRC per datagram.", "RTU over UDP/IP：1データグラムにCRC付きRTUフレーム1つ。", "RTU over UDP/IP: 데이터그램당 CRC 포함 RTU 프레임 하나.", "RTU over UDP/IP: один кадр RTU с CRC в дейтаграмме.");
            Add(d, "本机主站可连接127.0.0.1。", "A local master can connect to 127.0.0.1.", "同じPCのマスターは127.0.0.1へ接続可能。", "같은 PC의 마스터는 127.0.0.1에 연결할 수 있습니다.", "Локальный Master может подключиться к 127.0.0.1.");
            Add(d, "RTU：8 数据位，自动校验 CRC；RS-485 使用自动收发转换器。四区同时有效。", "RTU: 8 data bits, automatic CRC. Use an auto-direction RS-485 converter. All four areas are active.", "RTU：8データビット、CRC自動処理。RS-485は自動方向切替変換器を使用。4領域すべて有効。", "RTU: 데이터 8비트, 자동 CRC. RS-485 자동 방향 전환 변환기 사용. 4영역 모두 활성.", "RTU: 8 бит, CRC автоматически. RS-485 с автопереключением направления. Все 4 области активны.");
            Add(d, "串口 MBAP 扩展：MBAP + PDU，不加 RTU CRC。按 Length 收帧，按 TID 对应响应。", "Serial MBAP extension: MBAP + PDU, no RTU CRC. Length frames messages; TID matches responses.", "シリアルMBAP拡張：MBAP + PDU、RTU CRCなし。Lengthで区切り、TIDで応答を照合。", "직렬 MBAP 확장: MBAP + PDU, RTU CRC 없음. Length로 프레임 구분, TID로 응답 대응.", "COM MBAP: MBAP + PDU, без RTU CRC. Границы по Length, соответствие ответов по TID.");
            Add(d, "ASCII：冒号 + HEX + LRC + CRLF，可选7/8数据位。请与对端的校验和停止位保持一致。", "ASCII: colon + HEX + LRC + CRLF; 7/8 data bits. Match peer parity and stop bits.", "ASCII：コロン + HEX + LRC + CRLF。7/8データビット。相手のパリティ・停止ビットに合わせてください。", "ASCII: 콜론 + HEX + LRC + CRLF, 데이터 7/8비트. 상대의 패리티/정지 비트와 맞추세요.", "ASCII: двоеточие + HEX + LRC + CRLF, 7/8 бит. Паритет и стоп-биты как у устройства.");
            Add(d, "远端IP/端口须与从机一致；本机联调使用127.0.0.1。TCP/UDP与RTU封装不可混用。", "Match the slave IP/port; use 127.0.0.1 for local tests. TCP/UDP and RTU framing must match.", "スレーブのIP/ポートに合わせる。同じPCは127.0.0.1。TCP/UDPとRTUの形式を混在させないでください。", "슬레이브 IP/포트와 일치, 로컬 테스트는 127.0.0.1. TCP/UDP와 RTU 프레임 형식을 맞추세요.", "IP/порт как у Slave; локально 127.0.0.1. Форматы TCP/UDP и RTU должны совпадать.");
            Add(d, "ASCII为冒号+HEX+LRC+CRLF；可选7/8数据位，校验和停止位与设备一致。", "ASCII: colon + HEX + LRC + CRLF; 7/8 data bits. Match device parity and stop bits.", "ASCIIはコロン+HEX+LRC+CRLF。7/8データビット。機器のパリティと停止ビットに合わせる。", "ASCII: 콜론+HEX+LRC+CRLF, 데이터 7/8비트. 장치의 패리티와 정지 비트에 맞추세요.", "ASCII: двоеточие+HEX+LRC+CRLF; 7/8 бит. Паритет и стоп-биты как у устройства.");
            Add(d, "串口MBAP扩展无CRC，严格按Length收帧并核对Transaction ID。", "Serial MBAP has no CRC; frames by Length and verifies Transaction ID.", "シリアルMBAP拡張はCRCなし。Lengthで受信しTransaction IDを照合。", "직렬 MBAP 확장은 CRC 없음. Length로 프레임 수신, Transaction ID 확인.", "COM MBAP без CRC; кадры по Length, проверка Transaction ID.");
            Add(d, "RTU使用8数据位与CRC16；波特率、校验、停止位与设备一致。", "RTU uses 8 data bits and CRC16. Match device baud, parity and stop bits.", "RTUは8データビットとCRC16。ボーレート・パリティ・停止ビットを機器に合わせる。", "RTU는 데이터 8비트와 CRC16 사용. 장치 전송 속도, 패리티, 정지 비트에 맞추세요.", "RTU: 8 бит и CRC16. Скорость, паритет и стоп-биты как у устройства.");
            return d;
        }
    }

    /// <summary>统一翻译消息内容及标题；系统标准按钮遵从Windows自己的界面语言。</summary>
    public static class LocalizedMessageBox
    {
        public static DialogResult Show(string text) { return MessageBox.Show(UiLanguage.Translate(text)); }
        public static DialogResult Show(string text, string caption) { return MessageBox.Show(UiLanguage.Translate(text), UiLanguage.Translate(caption)); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) { return MessageBox.Show(UiLanguage.Translate(text), UiLanguage.Translate(caption), buttons); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return MessageBox.Show(UiLanguage.Translate(text), UiLanguage.Translate(caption), buttons, icon); }
        public static DialogResult Show(IWin32Window owner, string text) { return MessageBox.Show(owner, UiLanguage.Translate(text)); }
        public static DialogResult Show(IWin32Window owner, string text, string caption) { return MessageBox.Show(owner, UiLanguage.Translate(text), UiLanguage.Translate(caption)); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) { return MessageBox.Show(owner, UiLanguage.Translate(text), UiLanguage.Translate(caption), buttons); }
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) { return MessageBox.Show(owner, UiLanguage.Translate(text), UiLanguage.Translate(caption), buttons, icon); }
    }
}
