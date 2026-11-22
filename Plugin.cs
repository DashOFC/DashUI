using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace DashUI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Among Us.exe")]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.dash.dashui";
        public const string PluginName = "DashUI";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log { get; private set; }
        internal static Harmony HarmonyInstance { get; private set; }

        public override void Load()
        {
            Log = base.Log;
            HarmonyInstance = new Harmony(PluginGuid);

            DashConfig.Initialize(Config, Paths.ConfigPath);
            ClassInjector.RegisterTypeInIl2Cpp<DashUIComponent>();

            var obj = new GameObject("DashUI_Object");
            obj.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(obj);
            obj.AddComponent<DashUIComponent>();

            HarmonyInstance.PatchAll();
            Log.LogInfo($"{PluginName} v{PluginVersion} carregado com sucesso.");
        }
    }

    internal enum TaskCounterMode
    {
        RealTime = 0,
        MeetingOnly = 1
    }

    internal enum ControlScheme
    {
        Mouse = 0,
        MouseAndKeyboard = 1
    }

    internal enum UiElementCategory
    {
        MenusAndWindows = 0,
        Buttons = 1,
        LobbyTablet = 2,
        MeetingTablet = 3,
        Others = 4
    }

    internal static class DashConfig
    {
        public static ConfigEntry<float> MusicVolume { get; private set; }
        public static ConfigEntry<float> SfxVolume { get; private set; }
        public static ConfigEntry<int> ControlModeIndex { get; private set; }
        public static ConfigEntry<bool> WalkWithMouse { get; private set; }
        public static ConfigEntry<bool> BepInExConsole { get; private set; }

        public static ConfigEntry<int> ResolutionIndex { get; private set; }
        public static ConfigEntry<bool> VSyncEnabled { get; private set; }

        public static ConfigEntry<float> ScaleMenus { get; private set; }
        public static ConfigEntry<float> ScaleButtons { get; private set; }
        public static ConfigEntry<float> ScaleLobbyTablet { get; private set; }
        public static ConfigEntry<float> ScaleMeetingTablet { get; private set; }
        public static ConfigEntry<float> ScaleOthers { get; private set; }

        public static ConfigEntry<int> TaskCounterModeIndex { get; private set; }
        public static ConfigEntry<float> TaskCounterScale { get; private set; }
        public static ConfigEntry<float> TaskCounterPosX { get; private set; }
        public static ConfigEntry<float> TaskCounterPosY { get; private set; }

        public static string NotesFilePath { get; private set; }

        public static void Initialize(ConfigFile config, string configDir)
        {
            MusicVolume = config.Bind("Geral", "Musica", 0.8f, "Volume da música (0.0 a 1.0)");
            SfxVolume = config.Bind("Geral", "EfeitosSonoros", 0.8f, "Volume dos efeitos sonoros (0.0 a 1.0)");
            ControlModeIndex = config.Bind("Geral", "ModoControle", 0, "0 = Mouse, 1 = Mouse e teclado");
            WalkWithMouse = config.Bind("Geral", "AndarComMouse", true, "Andar com o mouse");
            BepInExConsole = config.Bind("Geral", "ConsoleBepInEx", false, "Console do BepInEx");

            ResolutionIndex = config.Bind("Tela", "IndiceResolucao", -1, "Índice da resolução selecionada");
            VSyncEnabled = config.Bind("Tela", "VSync", true, "Sincronização vertical (VSync)");

            ScaleMenus = config.Bind("UI_Escala", "MenusEJanelas", 1.0f, "Escala de menus e janelas");
            ScaleButtons = config.Bind("UI_Escala", "Botoes", 1.0f, "Escala de botões");
            ScaleLobbyTablet = config.Bind("UI_Escala", "TabletLobby", 1.0f, "Escala do tablet no lobby");
            ScaleMeetingTablet = config.Bind("UI_Escala", "TabletReuniao", 1.0f, "Escala do tablet na reunião");
            ScaleOthers = config.Bind("UI_Escala", "Outros", 1.0f, "Escala de outros elementos de UI");

            TaskCounterModeIndex = config.Bind("Partida", "ModoContadorTarefas", 0, "0 = Em tempo real, 1 = Apenas por reunião");
            TaskCounterScale = config.Bind("Partida", "EscalaContadorTarefas", 1.0f, "Escala do contador de tarefas");
            TaskCounterPosX = config.Bind("Partida", "PosicaoXContadorTarefas", 16f, "Posição X do contador de tarefas");
            TaskCounterPosY = config.Bind("Partida", "PosicaoYContadorTarefas", 16f, "Posição Y do contador de tarefas");

            Directory.CreateDirectory(configDir);
            NotesFilePath = Path.Combine(configDir, "DashUI_Notas.txt");
            if (!File.Exists(NotesFilePath))
            {
                File.WriteAllText(NotesFilePath, string.Empty);
            }
        }

        public static void ResetUiScalesToDefault()
        {
            ScaleMenus.Value = 1.0f;
            ScaleButtons.Value = 1.0f;
            ScaleLobbyTablet.Value = 1.0f;
            ScaleMeetingTablet.Value = 1.0f;
            ScaleOthers.Value = 1.0f;
        }
    }

    internal static class TaskProgressTracker
    {
        private static int _cachedCompleted;
        private static int _cachedTotal;
        private static bool _hasSnapshot;

        public static void NotifyMeetingStartOrEnd()
        {
            ComputeCurrentTasks(out int completed, out int total);
            _cachedCompleted = completed;
            _cachedTotal = total;
            _hasSnapshot = true;
        }

        public static void ResetSnapshot()
        {
            _cachedCompleted = 0;
            _cachedTotal = 0;
            _hasSnapshot = false;
        }

        public static bool TryGetTaskDisplay(out string formattedText)
        {
            formattedText = string.Empty;

            if (!IsInActiveMatchWithTaskBar(out int liveCompleted, out int liveTotal))
                return false;

            if (liveTotal <= 0)
                return false;

            var mode = (TaskCounterMode)DashConfig.TaskCounterModeIndex.Value;

            if (mode == TaskCounterMode.RealTime)
            {
                formattedText = $"{liveCompleted}/{liveTotal}";
                return true;
            }

            if (!_hasSnapshot)
            {
                _cachedCompleted = liveCompleted;
                _cachedTotal = liveTotal;
                _hasSnapshot = true;
            }

            int displayTotal = _cachedTotal > 0 ? _cachedTotal : liveTotal;
            int displayCompleted = Math.Min(_cachedCompleted, displayTotal);
            formattedText = $"{displayCompleted}/{displayTotal}";
            return true;
        }

        private static void ComputeCurrentTasks(out int completed, out int total)
        {
            completed = 0;
            total = 0;

            var hud = HudManager.InstanceExists ? HudManager.Instance : null;
            if (hud == null || hud.TaskPanel == null)
                return;

            var taskPanel = hud.TaskPanel;
            if (!taskPanel.gameObject.activeInHierarchy)
                return;

            var player = PlayerControl.LocalPlayer;
            if (player == null || player.myTasks == null)
                return;

            for (int i = 0; i < player.myTasks.Count; i++)
            {
                var task = player.myTasks[i];
                if (task == null) continue;

                total++;
                if (task.IsComplete)
                {
                    completed++;
                }
            }
        }

        private static bool IsInActiveMatchWithTaskBar(out int completed, out int total)
        {
            completed = 0;
            total = 0;

            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.IsGameStarted)
                return false;

            var hud = HudManager.InstanceExists ? HudManager.Instance : null;
            if (hud == null || hud.TaskPanel == null || !hud.TaskPanel.gameObject.activeInHierarchy)
                return false;

            ComputeCurrentTasks(out completed, out total);
            return total > 0;
        }
    }

    internal sealed class DashUIComponent : MonoBehaviour
    {
        public static DashUIComponent Instance { get; private set; }

        private bool _menuOpen;
        private int _activeTab; // 0 = Geral, 1 = Tela, 2 = Chat, 3 = Partida
        private int _selectedCategory; // 0..4

        private string _notesBuffer = string.Empty;
        private string _notesStatusMessage = string.Empty;
        private float _notesStatusTimer;

        private Vector2 _tabScrollPos = Vector2.zero;
        private Vector2 _notesScrollPos = Vector2.zero;

        private Resolution[] _resolutions = Array.Empty<Resolution>();
        private string[] _resolutionLabels = Array.Empty<string>();

        private bool _stylesInitialized;
        private Texture2D _darkBgTex;
        private Texture2D _panelBgTex;
        private Texture2D _headerBgTex;
        private Texture2D _buttonTex;
        private Texture2D _buttonHoverTex;
        private Texture2D _activeTabTex;

        private GUIStyle _windowStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _tabStyle;
        private GUIStyle _tabActiveStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _textAreaStyle;
        private GUIStyle _taskCounterStyle;

        public DashUIComponent(IntPtr ptr) : base(ptr) { }

        private void Awake()
        {
            Instance = this;
            RefreshResolutions();
            LoadNotesFromDisk();
            ApplyStartupSettings();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            CleanupTextures();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                _menuOpen = !_menuOpen;
                if (_menuOpen)
                {
                    RefreshResolutions();
                    LoadNotesFromDisk();
                }
            }

            if (_notesStatusTimer > 0f)
            {
                _notesStatusTimer -= Time.unscaledDeltaTime;
                if (_notesStatusTimer <= 0f)
                {
                    _notesStatusMessage = string.Empty;
                }
            }

            ApplyUiElementScales();
        }

        private void OnGUI()
        {
            EnsureStyles();

            DrawTaskCounter();

            if (!_menuOpen)
                return;

            float menuScale = Mathf.Clamp(DashConfig.ScaleMenus.Value, 0.75f, 1.35f);
            float baseWidth = 760f;
            float baseHeight = 470f;

            float screenW = Screen.width;
            float screenH = Screen.height;

            float winW = baseWidth * menuScale;
            float winH = baseHeight * menuScale;
            float winX = (screenW - winW) * 0.5f;
            float winY = (screenH - winH) * 0.5f;

            // Fundo escurecido centralizado
            GUI.Box(new Rect(0f, 0f, screenW, screenH), GUIContent.none, _darkBgTex != null ? _windowStyle : GUI.skin.box);

            Rect mainRect = new Rect(winX, winY, winW, winH);
            GUI.Box(mainRect, GUIContent.none, _windowStyle);

            float pad = 14f * menuScale;
            float headerH = 38f * menuScale;
            float tabH = 32f * menuScale;
            float footerH = 42f * menuScale;

            // Cabeçalho: Título "Configurações" + Botão "menu original"
            Rect titleRect = new Rect(winX + pad, winY + 8f * menuScale, winW * 0.65f, headerH);
            GUI.Label(titleRect, "Configurações", _headerStyle);

            float origBtnW = 145f * menuScale;
            Rect origBtnRect = new Rect(winX + winW - pad - origBtnW, winY + 10f * menuScale, origBtnW, 28f * menuScale);
            if (GUI.Button(origBtnRect, "menu original", _buttonStyle))
            {
                OpenOriginalSettingsMenu();
            }

            // Abas: Geral, Tela, Chat, Partida
            string[] tabs = { "Geral", "Tela", "Chat", "Partida" };
            float tabAreaY = winY + headerH + 10f * menuScale;
            float tabW = (winW - pad * 2f - (tabs.Length - 1) * 6f * menuScale) / tabs.Length;

            for (int i = 0; i < tabs.Length; i++)
            {
                Rect tRect = new Rect(
                    winX + pad + i * (tabW + 6f * menuScale),
                    tabAreaY,
                    tabW,
                    tabH
                );

                GUIStyle styleToUse = (_activeTab == i) ? _tabActiveStyle : _tabStyle;
                if (GUI.Button(tRect, tabs[i], styleToUse))
                {
                    _activeTab = i;
                    _tabScrollPos = Vector2.zero;
                }
            }

            // Área de conteúdo
            float contentY = tabAreaY + tabH + 12f * menuScale;
            float contentH = winH - (contentY - winY) - footerH - 16f * menuScale;
            Rect contentRect = new Rect(winX + pad, contentY, winW - pad * 2f, contentH);
            GUI.Box(contentRect, GUIContent.none, _windowStyle);

            Rect innerContent = new Rect(
                contentRect.x + 12f * menuScale,
                contentRect.y + 10f * menuScale,
                contentRect.width - 24f * menuScale,
                contentRect.height - 20f * menuScale
            );

            _tabScrollPos = GUI.BeginScrollView(innerContent, _tabScrollPos, new Rect(0f, 0f, innerContent.width - 16f, 520f * menuScale));

            switch (_activeTab)
            {
                case 0:
                    DrawGeneralTab(innerContent.width - 20f, menuScale);
                    break;
                case 1:
                    DrawDisplayTab(innerContent.width - 20f, menuScale);
                    break;
                case 2:
                    DrawChatTab(innerContent.width - 20f, menuScale);
                    break;
                case 3:
                    DrawMatchTab(innerContent.width - 20f, menuScale);
                    break;
            }

            GUI.EndScrollView();

            // Rodapé fixo: Voltar ao jogo (esquerda) e Sair da partida (direita)
            float footerY = winY + winH - footerH - 8f * menuScale;
            float footBtnW = 165f * menuScale;
            float footBtnH = 30f * menuScale;

            Rect resumeRect = new Rect(winX + pad, footerY, footBtnW, footBtnH);
            if (GUI.Button(resumeRect, "Voltar ao jogo", _buttonStyle))
            {
                _menuOpen = false;
            }

            Rect leaveRect = new Rect(winX + winW - pad - footBtnW, footerY, footBtnW, footBtnH);
            if (GUI.Button(leaveRect, "Sair da partida", _buttonStyle))
            {
                LeaveCurrentMatch();
            }
        }

        private void DrawTaskCounter()
        {
            if (!TaskProgressTracker.TryGetTaskDisplay(out string labelText))
                return;

            float scale = Mathf.Clamp(DashConfig.TaskCounterScale.Value, 0.5f, 2.5f);
            float x = DashConfig.TaskCounterPosX.Value;
            float y = DashConfig.TaskCounterPosY.Value;

            float w = 110f * scale;
            float h = 32f * scale;

            _taskCounterStyle.fontSize = Mathf.RoundToInt(16f * scale);
            GUI.Label(new Rect(x, y, w, h), labelText, _taskCounterStyle);
        }

        private void DrawGeneralTab(float width, float scale)
        {
            float y = 0f;
            float rowH = 28f * scale;
            float gap = 10f * scale;

            // Música (0% - 100%)
            int musicPct = Mathf.RoundToInt(DashConfig.MusicVolume.Value * 100f);
            GUI.Label(new Rect(0f, y, 160f * scale, rowH), $"Música: {musicPct}%", _labelStyle);
            float newMusic = GUI.HorizontalSlider(
                new Rect(170f * scale, y + 7f * scale, width - 170f * scale, 18f * scale),
                DashConfig.MusicVolume.Value,
                0f,
                1f
            );
            if (!Mathf.Approximately(newMusic, DashConfig.MusicVolume.Value))
            {
                DashConfig.MusicVolume.Value = Mathf.Clamp01(newMusic);
                ApplyAudioVolumes();
            }
            y += rowH + gap;

            // Efeitos sonoros (0% - 100%)
            int sfxPct = Mathf.RoundToInt(DashConfig.SfxVolume.Value * 100f);
            GUI.Label(new Rect(0f, y, 160f * scale, rowH), $"Efeitos sonoros: {sfxPct}%", _labelStyle);
            float newSfx = GUI.HorizontalSlider(
                new Rect(170f * scale, y + 7f * scale, width - 170f * scale, 18f * scale),
                DashConfig.SfxVolume.Value,
                0f,
                1f
            );
            if (!Mathf.Approximately(newSfx, DashConfig.SfxVolume.Value))
            {
                DashConfig.SfxVolume.Value = Mathf.Clamp01(newSfx);
                ApplyAudioVolumes();
            }
            y += rowH + gap * 1.4f;

            // Seletor de Controle: Mouse | Mouse e teclado
            GUI.Label(new Rect(0f, y, 160f * scale, rowH), "Controle:", _labelStyle);
            float halfW = (width - 175f * scale) * 0.5f;

            bool isMouseOnly = DashConfig.ControlModeIndex.Value == (int)ControlScheme.Mouse;
            if (GUI.Button(new Rect(170f * scale, y, halfW, rowH), "Mouse", isMouseOnly ? _tabActiveStyle : _buttonStyle))
            {
                DashConfig.ControlModeIndex.Value = (int)ControlScheme.Mouse;
                ApplyControlScheme();
            }

            bool isMouseAndKb = DashConfig.ControlModeIndex.Value == (int)ControlScheme.MouseAndKeyboard;
            if (GUI.Button(new Rect(175f * scale + halfW, y, halfW, rowH), "Mouse e teclado", isMouseAndKb ? _tabActiveStyle : _buttonStyle))
            {
                DashConfig.ControlModeIndex.Value = (int)ControlScheme.MouseAndKeyboard;
                ApplyControlScheme();
            }
            y += rowH + gap * 1.4f;

            // Checkbox: Andar com o mouse
            bool walkMouse = GUI.Toggle(
                new Rect(0f, y, width, rowH),
                DashConfig.WalkWithMouse.Value,
                " Andar com o mouse",
                _labelStyle
            );
            if (walkMouse != DashConfig.WalkWithMouse.Value)
            {
                DashConfig.WalkWithMouse.Value = walkMouse;
                ApplyControlScheme();
            }
            y += rowH + gap;

            // Checkbox: Console do BepInEx (janela preta)
            bool consoleOn = GUI.Toggle(
                new Rect(0f, y, width, rowH),
                DashConfig.BepInExConsole.Value,
                " Console do BepInEx (janela preta)",
                _labelStyle
            );
            if (consoleOn != DashConfig.BepInExConsole.Value)
            {
                DashConfig.BepInExConsole.Value = consoleOn;
                ApplyConsoleVisibility(consoleOn);
            }
        }

        private void DrawDisplayTab(float width, float scale)
        {
            float y = 0f;
            float rowH = 28f * scale;
            float gap = 10f * scale;

            // Resolução
            GUI.Label(new Rect(0f, y, 140f * scale, rowH), "Resolução:", _labelStyle);
            if (_resolutionLabels.Length > 0)
            {
                int currentIdx = Mathf.Clamp(DashConfig.ResolutionIndex.Value, 0, _resolutionLabels.Length - 1);
                float btnW = 36f * scale;
                float labelW = width - 140f * scale - btnW * 2f - 12f * scale;

                if (GUI.Button(new Rect(145f * scale, y, btnW, rowH), "<", _buttonStyle))
                {
                    currentIdx = (currentIdx - 1 + _resolutionLabels.Length) % _resolutionLabels.Length;
                    ApplyResolutionIndex(currentIdx);
                }

                GUI.Label(
                    new Rect(145f * scale + btnW + 6f * scale, y, labelW, rowH),
                    _resolutionLabels[currentIdx],
                    _labelStyle
                );

                if (GUI.Button(new Rect(145f * scale + btnW + 12f * scale + labelW, y, btnW, rowH), ">", _buttonStyle))
                {
                    currentIdx = (currentIdx + 1) % _resolutionLabels.Length;
                    ApplyResolutionIndex(currentIdx);
                }
            }
            else
            {
                GUI.Label(new Rect(145f * scale, y, width - 145f * scale, rowH), $"{Screen.width}x{Screen.height}", _labelStyle);
            }
            y += rowH + gap;

            // Toggle VSync
            bool vsync = GUI.Toggle(
                new Rect(0f, y, width, rowH),
                DashConfig.VSyncEnabled.Value,
                " VSync",
                _labelStyle
            );
            if (vsync != DashConfig.VSyncEnabled.Value)
            {
                DashConfig.VSyncEnabled.Value = vsync;
                QualitySettings.vSyncCount = vsync ? 1 : 0;
            }
            y += rowH + gap * 1.4f;

            // Categorias de UI
            string[] categories =
            {
                "Menus e janelas",
                "Botões",
                "Tablet Lobby",
                "Tablet Reunião",
                "Outros"
            };

            GUI.Label(new Rect(0f, y, width, 24f * scale), "Personalização de elementos da UI:", _labelStyle);
            y += 26f * scale;

            float catW = (width - (categories.Length - 1) * 4f * scale) / categories.Length;
            for (int i = 0; i < categories.Length; i++)
            {
                Rect cRect = new Rect(i * (catW + 4f * scale), y, catW, 26f * scale);
                if (GUI.Button(cRect, categories[i], _selectedCategory == i ? _tabActiveStyle : _buttonStyle))
                {
                    _selectedCategory = i;
                }
            }
            y += 34f * scale;

            float currentScale = GetCategoryScale(_selectedCategory);
            int scalePct = Mathf.RoundToInt(currentScale * 100f);

            GUI.Label(new Rect(0f, y, 180f * scale, rowH), $"Tamanho ({categories[_selectedCategory]}): {scalePct}%", _labelStyle);
            float updatedScale = GUI.HorizontalSlider(
                new Rect(185f * scale, y + 7f * scale, width - 185f * scale, 18f * scale),
                currentScale,
                0.5f,
                1.5f
            );
            if (!Mathf.Approximately(updatedScale, currentScale))
            {
                SetCategoryScale(_selectedCategory, Mathf.Clamp(updatedScale, 0.5f, 1.5f));
            }
            y += rowH + gap * 1.2f;

            // Botões Salvar e Redefinir
            float actionW = 130f * scale;
            if (GUI.Button(new Rect(0f, y, actionW, rowH), "Salvar", _buttonStyle))
            {
                Plugin.Instance?.Config?.Save();
                ApplyUiElementScales();
            }

            if (GUI.Button(new Rect(actionW + 10f * scale, y, actionW, rowH), "Redefinir", _buttonStyle))
            {
                DashConfig.ResetUiScalesToDefault();
                Plugin.Instance?.Config?.Save();
                ApplyUiElementScales();
            }
        }

        private void DrawChatTab(float width, float scale)
        {
            float y = 0f;
            float areaH = 210f * scale;

            GUI.Label(new Rect(0f, y, width, 24f * scale), "Notas / Bloco rápido (DashUI_Notas.txt):", _labelStyle);
            y += 26f * scale;

            _notesScrollPos = GUI.BeginScrollView(
                new Rect(0f, y, width, areaH),
                _notesScrollPos,
                new Rect(0f, 0f, width - 16f, Math.Max(areaH, 320f * scale))
            );

            _notesBuffer = GUI.TextArea(
                new Rect(0f, 0f, width - 20f, Math.Max(areaH, 320f * scale)),
                _notesBuffer ?? string.Empty,
                4000,
                _textAreaStyle
            );

            GUI.EndScrollView();
            y += areaH + 10f * scale;

            float btnW = 120f * scale;
            float btnH = 28f * scale;

            if (GUI.Button(new Rect(0f, y, btnW, btnH), "Salvar", _buttonStyle))
            {
                SaveNotesToDisk();
            }

            if (GUI.Button(new Rect(btnW + 10f * scale, y, btnW, btnH), "Limpar", _buttonStyle))
            {
                _notesBuffer = string.Empty;
                SaveNotesToDisk();
            }

            if (!string.IsNullOrEmpty(_notesStatusMessage))
            {
                GUI.Label(
                    new Rect((btnW + 10f * scale) * 2f, y, width - (btnW + 10f * scale) * 2f, btnH),
                    _notesStatusMessage,
                    _labelStyle
                );
            }
        }

        private void DrawMatchTab(float width, float scale)
        {
            float y = 0f;
            float rowH = 30f * scale;
            float gap = 12f * scale;

            GUI.Label(new Rect(0f, y, width, 26f * scale), "Contador de Tarefas:", _labelStyle);
            y += 30f * scale;

            float halfW = (width - 10f * scale) * 0.5f;
            bool isRealTime = DashConfig.TaskCounterModeIndex.Value == (int)TaskCounterMode.RealTime;

            if (GUI.Button(new Rect(0f, y, halfW, rowH), "Em tempo real", isRealTime ? _tabActiveStyle : _buttonStyle))
            {
                DashConfig.TaskCounterModeIndex.Value = (int)TaskCounterMode.RealTime;
                Plugin.Instance?.Config?.Save();
            }

            bool isMeetingOnly = DashConfig.TaskCounterModeIndex.Value == (int)TaskCounterMode.MeetingOnly;
            if (GUI.Button(new Rect(halfW + 10f * scale, y, halfW, rowH), "Apenas por reunião", isMeetingOnly ? _tabActiveStyle : _buttonStyle))
            {
                DashConfig.TaskCounterModeIndex.Value = (int)TaskCounterMode.MeetingOnly;
                TaskProgressTracker.NotifyMeetingStartOrEnd();
                Plugin.Instance?.Config?.Save();
            }
            y += rowH + gap * 1.4f;

            // Escala do contador
            float counterScale = DashConfig.TaskCounterScale.Value;
            GUI.Label(new Rect(0f, y, 180f * scale, rowH), $"Escala do contador: {counterScale:0.0}x", _labelStyle);
            float newCounterScale = GUI.HorizontalSlider(
                new Rect(185f * scale, y + 8f * scale, width - 185f * scale, 18f * scale),
                counterScale,
                0.7f,
                2.0f
            );
            if (!Mathf.Approximately(newCounterScale, counterScale))
            {
                DashConfig.TaskCounterScale.Value = Mathf.Clamp(newCounterScale, 0.7f, 2.0f);
            }
        }

        private float GetCategoryScale(int catIndex)
        {
            return catIndex switch
            {
                0 => DashConfig.ScaleMenus.Value,
                1 => DashConfig.ScaleButtons.Value,
                2 => DashConfig.ScaleLobbyTablet.Value,
                3 => DashConfig.ScaleMeetingTablet.Value,
                4 => DashConfig.ScaleOthers.Value,
                _ => 1.0f
            };
        }

        private void SetCategoryScale(int catIndex, float value)
        {
            switch (catIndex)
            {
                case 0: DashConfig.ScaleMenus.Value = value; break;
                case 1: DashConfig.ScaleButtons.Value = value; break;
                case 2: DashConfig.ScaleLobbyTablet.Value = value; break;
                case 3: DashConfig.ScaleMeetingTablet.Value = value; break;
                case 4: DashConfig.ScaleOthers.Value = value; break;
            }
        }

        private void ApplyStartupSettings()
        {
            ApplyAudioVolumes();
            QualitySettings.vSyncCount = DashConfig.VSyncEnabled.Value ? 1 : 0;

            if (DashConfig.ResolutionIndex.Value >= 0 && DashConfig.ResolutionIndex.Value < _resolutions.Length)
            {
                ApplyResolutionIndex(DashConfig.ResolutionIndex.Value);
            }

            ApplyControlScheme();
            ApplyConsoleVisibility(DashConfig.BepInExConsole.Value);
        }

        private void RefreshResolutions()
        {
            var resList = Screen.resolutions;
            if (resList == null || resList.Length == 0)
            {
                _resolutions = Array.Empty<Resolution>();
                _resolutionLabels = Array.Empty<string>();
                return;
            }

            var unique = new List<Resolution>();
            var labels = new List<string>();

            for (int i = 0; i < resList.Length; i++)
            {
                var r = resList[i];
                string lbl = $"{r.width}x{r.height}";
                if (!labels.Contains(lbl))
                {
                    unique.Add(r);
                    labels.Add(lbl);
                }
            }

            _resolutions = unique.ToArray();
            _resolutionLabels = labels.ToArray();
        }

        private void ApplyResolutionIndex(int index)
        {
            if (_resolutions == null || _resolutions.Length == 0) return;
            index = Mathf.Clamp(index, 0, _resolutions.Length - 1);
            DashConfig.ResolutionIndex.Value = index;

            var res = _resolutions[index];
            Screen.SetResolution(res.width, res.height, Screen.fullScreenMode);
        }

        private void ApplyAudioVolumes()
        {
            if (SoundManager.Instance != null)
            {
                SoundManager.Instance.MusicVolume = DashConfig.MusicVolume.Value;
                SoundManager.Instance.SfxVolume = DashConfig.SfxVolume.Value;
            }
        }

        private void ApplyControlScheme()
        {
            bool walkMouse = DashConfig.WalkWithMouse.Value &&
                             (ControlScheme)DashConfig.ControlModeIndex.Value == ControlScheme.Mouse;

            var player = PlayerControl.LocalPlayer;
            if (player != null)
            {
                // Mantém compatível com configurações de movimento do jogo sem alterar papéis
                if (DestroyableSingleton<PlayerCustomizationMenu>.InstanceExists)
                {
                    // Sem alteração destrutiva
                }
            }

            // Aplica preferência no DataManager quando disponível
            try
            {
                if (DataManager.Instance != null && DataManager.Instance.Settings != null && DataManager.Instance.Settings.Input != null)
                {
                    DataManager.Instance.Settings.Input.InputMode = walkMouse ? ControlTypes.Mouse : ControlTypes.Keyboard;
                }
            }
            catch
            {
                // Ignora caso estrutura de Input varie entre builds
            }
        }

        private void ApplyConsoleVisibility(bool visible)
        {
            try
            {
                ConsoleManager.SetConsoleWindow(visible);
            }
            catch
            {
                // Seguro em ambientes sem console nativo alocado
            }
        }

        private void ApplyUiElementScales()
        {
            var hud = HudManager.InstanceExists ? HudManager.Instance : null;
            if (hud == null) return;

            if (hud.TaskPanel != null)
            {
                float s = Mathf.Clamp(DashConfig.ScaleOthers.Value, 0.5f, 1.5f);
                hud.TaskPanel.transform.localScale = new Vector3(s, s, 1f);
            }

            if (MeetingHud.Instance != null)
            {
                float ms = Mathf.Clamp(DashConfig.ScaleMeetingTablet.Value, 0.5f, 1.5f);
                MeetingHud.Instance.transform.localScale = new Vector3(ms, ms, 1f);
            }
        }

        private void OpenOriginalSettingsMenu()
        {
            _menuOpen = false;
            var hud = HudManager.InstanceExists ? HudManager.Instance : null;
            if (hud != null && hud.GameSettings != null)
            {
                hud.GameSettings.gameObject.SetActive(true);
            }
        }

        private void LeaveCurrentMatch()
        {
            _menuOpen = false;
            TaskProgressTracker.ResetSnapshot();

            if (AmongUsClient.Instance != null)
            {
                AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame);
            }
        }

        private void LoadNotesFromDisk()
        {
            try
            {
                if (!string.IsNullOrEmpty(DashConfig.NotesFilePath) && File.Exists(DashConfig.NotesFilePath))
                {
                    _notesBuffer = File.ReadAllText(DashConfig.NotesFilePath);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Falha ao carregar notas: {ex.Message}");
            }
        }

        private void SaveNotesToDisk()
        {
            try
            {
                File.WriteAllText(DashConfig.NotesFilePath, _notesBuffer ?? string.Empty);
                _notesStatusMessage = "Salvo!";
                _notesStatusTimer = 2.0f;
            }
            catch (Exception ex)
            {
                _notesStatusMessage = "Erro ao salvar";
                _notesStatusTimer = 2.5f;
                Plugin.Log?.LogWarning($"Falha ao salvar notas: {ex.Message}");
            }
        }

        private Texture2D MakeSolidTex(Color color)
        {
            var tex = new Texture2D(2, 2);
            tex.SetPixel(0, 0, color);
            tex.SetPixel(1, 0, color);
            tex.SetPixel(0, 1, color);
            tex.SetPixel(1, 1, color);
            tex.Apply();
            return tex;
        }

        private void EnsureStyles()
        {
            if (_stylesInitialized) return;

            _darkBgTex = MakeSolidTex(new Color(0.06f, 0.07f, 0.09f, 0.72f));
            _panelBgTex = MakeSolidTex(new Color(0.11f, 0.13f, 0.17f, 0.96f));
            _headerBgTex = MakeSolidTex(new Color(0.16f, 0.19f, 0.25f, 0.98f));
            _buttonTex = MakeSolidTex(new Color(0.20f, 0.24f, 0.31f, 0.95f));
            _buttonHoverTex = MakeSolidTex(new Color(0.28f, 0.34f, 0.44f, 0.98f));
            _activeTabTex = MakeSolidTex(new Color(0.15f, 0.52f, 0.78f, 0.98f));

            _windowStyle = new GUIStyle(GUI.skin.box);
            _windowStyle.normal.background = _panelBgTex;

            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.normal.background = _headerBgTex;
            _headerStyle.normal.textColor = Color.white;
            _headerStyle.fontSize = 18;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.alignment = TextAnchor.MiddleLeft;
            _headerStyle.padding = new RectOffset(10, 10, 4, 4);

            _tabStyle = new GUIStyle(GUI.skin.button);
            _tabStyle.normal.background = _buttonTex;
            _tabStyle.hover.background = _buttonHoverTex;
            _tabStyle.normal.textColor = new Color(0.88f, 0.90f, 0.94f, 1f);
            _tabStyle.hover.textColor = Color.white;
            _tabStyle.alignment = TextAnchor.MiddleCenter;

            _tabActiveStyle = new GUIStyle(_tabStyle);
            _tabActiveStyle.normal.background = _activeTabTex;
            _tabActiveStyle.hover.background = _activeTabTex;
            _tabActiveStyle.normal.textColor = Color.white;
            _tabActiveStyle.fontStyle = FontStyle.Bold;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.normal.textColor = new Color(0.92f, 0.94f, 0.97f, 1f);
            _labelStyle.alignment = TextAnchor.MiddleLeft;
            _labelStyle.wordWrap = true;

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.normal.background = _buttonTex;
            _buttonStyle.hover.background = _buttonHoverTex;
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.alignment = TextAnchor.MiddleCenter;

            _textAreaStyle = new GUIStyle(GUI.skin.textArea);
            _textAreaStyle.normal.background = _headerBgTex;
            _textAreaStyle.normal.textColor = Color.white;
            _textAreaStyle.wordWrap = true;

            _taskCounterStyle = new GUIStyle(GUI.skin.label);
            _taskCounterStyle.normal.textColor = Color.white;
            _taskCounterStyle.fontStyle = FontStyle.Bold;
            _taskCounterStyle.alignment = TextAnchor.MiddleLeft;

            _stylesInitialized = true;
        }

        private void CleanupTextures()
        {
            UnityEngine.Object.Destroy(_darkBgTex);
            UnityEngine.Object.Destroy(_panelBgTex);
            UnityEngine.Object.Destroy(_headerBgTex);
            UnityEngine.Object.Destroy(_buttonTex);
            UnityEngine.Object.Destroy(_buttonHoverTex);
            UnityEngine.Object.Destroy(_activeTabTex);
        }
    }

    [HarmonyPatch]
    internal static class MatchLifecyclePatches
    {
        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
        [HarmonyPostfix]
        private static void OnMeetingStart()
        {
            TaskProgressTracker.NotifyMeetingStartOrEnd();
        }

        [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.RpcVotingComplete))]
        [HarmonyPostfix]
        private static void OnMeetingEnd()
        {
            TaskProgressTracker.NotifyMeetingStartOrEnd();
        }

        [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame))]
        [HarmonyPrefix]
        private static void OnExitGame()
        {
            TaskProgressTracker.ResetSnapshot();
        }
    }
}
