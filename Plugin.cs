using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace DashUI
{
    [BepInPlugin("com.dash.dashui", "DashUI", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public enum ModoContadorTarefas
        {
            TempoReal = 0,
            ApenasReuniao = 1
        }

        public enum ElementoUI
        {
            MenusEJanelas = 0,
            Botoes = 1,
            TabletLobby = 2,
            TabletReuniao = 3,
            OutrosElementos = 4
        }

        public static Plugin Instance;

        // Configs persistentes
        public ConfigEntry<int> ModoContadorCfg;
        public ConfigEntry<float> MusicaVolumeCfg;
        public ConfigEntry<float> SfxVolumeCfg;
        public ConfigEntry<int> ModoControleCfg; // 0 = Mouse, 1 = Mouse e teclado
        public ConfigEntry<bool> AndarComMouseCfg;
        public ConfigEntry<bool> ConsoleBepInExCfg;
        public ConfigEntry<int> ResolucaoIndexCfg;
        public ConfigEntry<bool> VSyncCfg;

        public static bool MenuAberto = false;
        public static int AbaAtual = 0; // 0 = Geral, 1 = Tela, 2 = Chat, 3 = Partida
        public static ElementoUI ElementoSelecionado = ElementoUI.MenusEJanelas;

        public static int TarefasConcluidasExibidas = 0;
        public static int TarefasTotaisExibidas = 0;
        private static bool estavaEmReuniao = false;

        public static string TextoNotas = "";
        private static string CaminhoNotas => Path.Combine(Paths.ConfigPath, "DashUI_Notas.txt");

        public static Dictionary<ElementoUI, (float escala, Vector2 offset)> LayoutElementos = new()
        {
            { ElementoUI.MenusEJanelas, (1.0f, Vector2.zero) },
            { ElementoUI.Botoes, (1.0f, Vector2.zero) },
            { ElementoUI.TabletLobby, (1.0f, Vector2.zero) },
            { ElementoUI.TabletReuniao, (1.0f, Vector2.zero) },
            { ElementoUI.OutrosElementos, (1.0f, Vector2.zero) }
        };

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        public override void Load()
        {
            Instance = this;

            ModoContadorCfg = Config.Bind("Partida", "ModoContador", (int)ModoContadorTarefas.TempoReal, "0 = Tempo real, 1 = Apenas por reuniao");
            MusicaVolumeCfg = Config.Bind("Geral", "MusicaVolume", 1.0f, "Volume da musica");
            SfxVolumeCfg = Config.Bind("Geral", "SfxVolume", 1.0f, "Volume dos efeitos sonoros");
            ModoControleCfg = Config.Bind("Geral", "ModoControle", 1, "0 = Mouse, 1 = Mouse e teclado");
            AndarComMouseCfg = Config.Bind("Geral", "AndarComMouse", false, "Andar com o mouse");
            ConsoleBepInExCfg = Config.Bind("Geral", "ConsoleBepInEx", true, "Console do BepInEx (janela preta)");
            ResolucaoIndexCfg = Config.Bind("Tela", "ResolucaoIndex", 0, "Resolucao");
            VSyncCfg = Config.Bind("Tela", "VSync", true, "VSync");

            foreach (ElementoUI el in Enum.GetValues(typeof(ElementoUI)))
            {
                float esc = Config.Bind("UI_" + el, "Escala", 1.0f).Value;
                float ox = Config.Bind("UI_" + el, "OffsetX", 0f).Value;
                float oy = Config.Bind("UI_" + el, "OffsetY", 0f).Value;
                LayoutElementos[el] = (esc, new Vector2(ox, oy));
            }

            CarregarNotasDoDisco();
            AplicarConfigGeral();

            ClassInjector.RegisterTypeInIl2Cpp<DashUIBehaviour>();
            var obj = new GameObject("DashUIOverlay");
            UnityEngine.Object.DontDestroyOnLoad(obj);
            obj.hideFlags = HideFlags.HideAndDontSave;
            obj.AddComponent<DashUIBehaviour>();

            var harmony = new Harmony("com.dash.dashui");
            harmony.PatchAll();

            Log.LogInfo("DashUI v1.0.0 carregado!");
        }

        public static void AplicarConfigGeral()
        {
            try
            {
                if (Instance != null)
                {
                    AudioListener.volume = Mathf.Clamp01(Instance.SfxVolumeCfg.Value);
                    QualitySettings.vSyncCount = Instance.VSyncCfg.Value ? 1 : 0;

                    IntPtr consoleWnd = GetConsoleWindow();
                    if (consoleWnd != IntPtr.Zero)
                    {
                        ShowWindow(consoleWnd, Instance.ConsoleBepInExCfg.Value ? SW_SHOW : SW_HIDE);
                    }
                }
            }
            catch { }
        }

        public static void CarregarNotasDoDisco()
        {
            try
            {
                TextoNotas = File.Exists(CaminhoNotas) ? File.ReadAllText(CaminhoNotas, Encoding.UTF8) : "";
            }
            catch { TextoNotas = ""; }
        }

        public static void SalvarNotasNoDisco()
        {
            try
            {
                File.WriteAllText(CaminhoNotas, TextoNotas ?? "", Encoding.UTF8);
            }
            catch { }
        }

        public static void LimparNotas()
        {
            TextoNotas = "";
            SalvarNotasNoDisco();
        }

        public static bool BarraTarefasPadraoAtiva()
        {
            try
            {
                var hud = HudManager.Instance;
                if (hud == null) return false;
                var prop = hud.GetType().GetProperty("TaskPanel", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                object taskPanel = prop?.GetValue(hud);
                if (taskPanel != null)
                {
                    var goProp = taskPanel.GetType().GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                    if (goProp?.GetValue(taskPanel) is GameObject go)
                        return go.activeInHierarchy;
                }
            }
            catch { }
            return true;
        }

        public static void AtualizarContagemTarefas()
        {
            try
            {
                var gameDataType = AccessTools.TypeByName("GameData");
                object instance = gameDataType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null) return;

                var completedProp = gameDataType.GetProperty("CompletedTasks", BindingFlags.Public | BindingFlags.Instance);
                var totalProp = gameDataType.GetProperty("TotalTasks", BindingFlags.Public | BindingFlags.Instance);

                int completed = completedProp != null ? Convert.ToInt32(completedProp.GetValue(instance)) : 0;
                int total = totalProp != null ? Convert.ToInt32(totalProp.GetValue(instance)) : 0;

                bool emReuniao = MeetingHud.Instance != null;
                var modo = Instance != null ? (ModoContadorTarefas)Instance.ModoContadorCfg.Value : ModoContadorTarefas.TempoReal;

                if (modo == ModoContadorTarefas.TempoReal)
                {
                    TarefasConcluidasExibidas = completed;
                    TarefasTotaisExibidas = total;
                }
                else
                {
                    if (emReuniao != estavaEmReuniao || TarefasTotaisExibidas == 0)
                    {
                        TarefasConcluidasExibidas = completed;
                        TarefasTotaisExibidas = total;
                    }
                }

                estavaEmReuniao = emReuniao;
            }
            catch { }
        }

        public static void VoltarMenuOriginal()
        {
            MenuAberto = false;
            try
            {
                var hud = HudManager.Instance;
                var menuBtn = hud?.GetType().GetProperty("GameMenu", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hud);
                menuBtn?.GetType().GetMethod("Open", BindingFlags.Public | BindingFlags.Instance)?.Invoke(menuBtn, null);
            }
            catch { }
        }

        public static void SairDaPartida()
        {
            MenuAberto = false;
            try { AmongUsClient.Instance?.ExitGame(DisconnectReasons.ExitGame); } catch { }
        }
    }

    public class DashUIBehaviour : MonoBehaviour
    {
        public DashUIBehaviour(IntPtr ptr) : base(ptr) { }

        private Texture2D texFundoJanela;
        private Texture2D texBotaoEscuro;
        private Texture2D texBotaoAtivo;
        private Texture2D texBotaoBranco;
        private Texture2D texBotaoVermelho;
        private Texture2D texSliderTrack;

        private void GarantirTexturas()
        {
            if (texFundoJanela != null) return;
            texFundoJanela = CriarTex(new Color(0.10f, 0.11f, 0.14f, 0.98f));
            texBotaoEscuro = CriarTex(new Color(0.18f, 0.19f, 0.23f, 1f));
            texBotaoAtivo = CriarTex(new Color(0.32f, 0.62f, 0.98f, 1f));
            texBotaoBranco = CriarTex(new Color(0.95f, 0.96f, 0.98f, 1f));
            texBotaoVermelho = CriarTex(new Color(0.86f, 0.22f, 0.22f, 1f));
            texSliderTrack = CriarTex(new Color(0.24f, 0.26f, 0.31f, 1f));
        }

        private Texture2D CriarTex(Color cor)
        {
            var t = new Texture2D(2, 2);
            t.SetPixels(new[] { cor, cor, cor, cor });
            t.Apply();
            return t;
        }

        private GUIStyle EstiloLabel(int tamanho, FontStyle estilo, Color cor, TextAnchor alinhamento = TextAnchor.UpperLeft)
        {
            var s = new GUIStyle(GUI.skin.label);
            s.fontSize = tamanho;
            s.fontStyle = estilo;
            s.alignment = alinhamento;
            s.normal.textColor = cor;
            return s;
        }

        private GUIStyle EstiloBotao(int tamanho, Texture2D fundo, Color corTexto)
        {
            var s = new GUIStyle(GUI.skin.button);
            s.fontSize = tamanho;
            s.fontStyle = FontStyle.Bold;
            s.normal.background = fundo;
            s.normal.textColor = corTexto;
            return s;
        }

        private GUIStyle EstiloToggle(int tamanho, Color corTexto)
        {
            var s = new GUIStyle(GUI.skin.toggle);
            s.fontSize = tamanho;
            s.fontStyle = FontStyle.Bold;
            s.normal.textColor = corTexto;
            s.onNormal.textColor = corTexto;
            return s;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1))
                Plugin.MenuAberto = !Plugin.MenuAberto;

            Plugin.AtualizarContagemTarefas();
        }

        private void OnGUI()
        {
            GarantirTexturas();

            // 1. Contador numérico de tarefas no canto superior esquerdo (ex: 6/120)
            if (Plugin.BarraTarefasPadraoAtiva() && Plugin.TarefasTotaisExibidas > 0)
            {
                var est = Plugin.LayoutElementos[Plugin.ElementoUI.OutrosElementos];
                Rect rTask = new Rect(18f + est.offset.x, 14f + est.offset.y, 130f * est.escala, 34f * est.escala);
                GUI.DrawTexture(rTask, texBotaoEscuro);

                var styleTask = EstiloLabel(Mathf.RoundToInt(18f * est.escala), FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                GUI.Label(rTask, $"✔ {Plugin.TarefasConcluidasExibidas}/{Plugin.TarefasTotaisExibidas}", styleTask);
            }

            if (!Plugin.MenuAberto) return;

            // 2. Janela central de Configurações
            float w = 440f;
            float h = 520f;
            Rect win = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            GUI.DrawTexture(win, texFundoJanela);

            GUI.Label(
                new Rect(win.x + 22, win.y + 18, 220, 34),
                "Configurações",
                EstiloLabel(22, FontStyle.Bold, Color.white)
            );

            var btnOriginalStyle = EstiloBotao(11, texBotaoEscuro, new Color(0.85f, 0.87f, 0.9f));
            if (GUI.Button(new Rect(win.x + w - 122, win.y + 20, 100, 26), "menu original", btnOriginalStyle))
            {
                Plugin.VoltarMenuOriginal();
                return;
            }

            // Abas: Geral, Tela, Chat, Partida
            string[] abas = { "Geral", "Tela", "Chat", "Partida" };
            float tabW = 74f;
            float gap = 10f;
            float startTabX = win.x + 22f;
            float tabY = win.y + 62f;

            for (int i = 0; i < abas.Length; i++)
            {
                bool ativa = Plugin.AbaAtual == i;
                var tabStyle = EstiloBotao(
                    13,
                    ativa ? texBotaoAtivo : texBotaoEscuro,
                    ativa ? Color.black : Color.white
                );
                if (GUI.Button(new Rect(startTabX + i * (tabW + gap), tabY, tabW, 28), abas[i], tabStyle))
                {
                    Plugin.AbaAtual = i;
                }
            }

            Rect body = new Rect(win.x + 22, win.y + 104, w - 44, h - 170);
            GUILayout.BeginArea(body);

            if (Plugin.AbaAtual == 0) DesenharGeral();
            else if (Plugin.AbaAtual == 1) DesenharTela();
            else if (Plugin.AbaAtual == 2) DesenharChat();
            else DesenharPartida();

            GUILayout.EndArea();

            // Botões inferiores: Voltar ao jogo | Sair da partida
            float footY = win.y + h - 56f;
            var btnVoltar = EstiloBotao(14, texBotaoBranco, new Color(0.08f, 0.09f, 0.12f));
            var btnSair = EstiloBotao(14, texBotaoVermelho, Color.white);

            if (GUI.Button(new Rect(win.x + 22, footY, 188, 36), "Voltar ao jogo", btnVoltar))
                Plugin.MenuAberto = false;

            if (GUI.Button(new Rect(win.x + w - 210, footY, 188, 36), "Sair da partida", btnSair))
                Plugin.SairDaPartida();
        }

        private void DesenharSliderPorcentagem(string titulo, float valor, Action<float> onChange)
        {
            var labelStyle = EstiloLabel(14, FontStyle.Bold, Color.white);
            GUILayout.Label(titulo, labelStyle);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUI.skin.horizontalSlider.normal.background = texSliderTrack;
            GUI.skin.horizontalSliderThumb.normal.background = texBotaoBranco;
            float novo = GUILayout.HorizontalSlider(valor, 0f, 1f, GUILayout.Height(18));
            GUILayout.Space(8);
            GUILayout.Label($"{Mathf.RoundToInt(novo * 100f)}%", labelStyle, GUILayout.Width(48));
            GUILayout.EndHorizontal();

            if (!Mathf.Approximately(novo, valor))
                onChange(novo);
        }

        private void DesenharGeral()
        {
            var inst = Plugin.Instance;
            if (inst == null) return;

            DesenharSliderPorcentagem("Música", inst.MusicaVolumeCfg.Value, v =>
            {
                inst.MusicaVolumeCfg.Value = v;
                Plugin.AplicarConfigGeral();
            });

            GUILayout.Space(10);
            DesenharSliderPorcentagem("Efeitos sonoros", inst.SfxVolumeCfg.Value, v =>
            {
                inst.SfxVolumeCfg.Value = v;
                Plugin.AplicarConfigGeral();
            });

            GUILayout.Space(12);
            var labelStyle = EstiloLabel(14, FontStyle.Bold, Color.white);
            GUILayout.Label("Controle", labelStyle);
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            string[] controles = { "Mouse", "Mouse e teclado" };
            for (int i = 0; i < controles.Length; i++)
            {
                bool sel = inst.ModoControleCfg.Value == i;
                var st = EstiloBotao(13, sel ? texBotaoAtivo : texBotaoBranco, new Color(0.08f, 0.09f, 0.12f));
                if (GUILayout.Button(controles[i], st, GUILayout.Height(30)))
                    inst.ModoControleCfg.Value = i;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(14);
            var toggleStyle = EstiloToggle(14, Color.white);
            inst.AndarComMouseCfg.Value = GUILayout.Toggle(inst.AndarComMouseCfg.Value, "  Andar com o mouse", toggleStyle);

            GUILayout.Space(8);
            bool novoConsole = GUILayout.Toggle(inst.ConsoleBepInExCfg.Value, "  Console do BepInEx (janela preta)", toggleStyle);
            if (novoConsole != inst.ConsoleBepInExCfg.Value)
            {
                inst.ConsoleBepInExCfg.Value = novoConsole;
                Plugin.AplicarConfigGeral();
            }
        }

        private void DesenharTela()
        {
            var inst = Plugin.Instance;
            if (inst == null) return;

            var labelStyle = EstiloLabel(14, FontStyle.Bold, Color.white);
            GUILayout.Label("Resolução", labelStyle);

            var resList = Screen.resolutions;
            string[] nomesRes = resList.Length > 0
                ? resList.Select(r => $"{r.width}x{r.height}").Distinct().Take(4).ToArray()
                : new[] { $"{Screen.width}x{Screen.height}" };

            int idx = Mathf.Clamp(inst.ResolucaoIndexCfg.Value, 0, nomesRes.Length - 1);
            int novoIdx = GUILayout.SelectionGrid(idx, nomesRes, 2, GUI.skin.button, GUILayout.Height(52));
            if (novoIdx != idx)
            {
                inst.ResolucaoIndexCfg.Value = novoIdx;
                if (resList.Length > novoIdx)
                    Screen.SetResolution(resList[novoIdx].width, resList[novoIdx].height, Screen.fullScreen);
            }

            GUILayout.Space(8);
            var toggleStyle = EstiloToggle(14, Color.white);
            bool novoVsync = GUILayout.Toggle(inst.VSyncCfg.Value, "  VSync", toggleStyle);
            if (novoVsync != inst.VSyncCfg.Value)
            {
                inst.VSyncCfg.Value = novoVsync;
                Plugin.AplicarConfigGeral();
            }

            GUILayout.Space(8);
            GUILayout.Label("Elemento UI", labelStyle);
            string[] els = { "Menus e janelas", "Botões", "Tablet Lobby", "Tablet Reunião", "Outros" };
            int elSel = GUILayout.SelectionGrid((int)Plugin.ElementoSelecionado, els, 2, GUI.skin.button, GUILayout.Height(66));
            Plugin.ElementoSelecionado = (Plugin.ElementoUI)elSel;

            var cur = Plugin.LayoutElementos[Plugin.ElementoSelecionado];
            float esc = GUILayout.HorizontalSlider(cur.escala, 0.6f, 1.6f);
            Plugin.LayoutElementos[Plugin.ElementoSelecionado] = (esc, cur.offset);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Salvar", GUILayout.Height(26)))
            {
                inst.Config.Bind("UI_" + Plugin.ElementoSelecionado, "Escala", 1.0f).Value = esc;
                inst.Config.Save();
            }
            if (GUILayout.Button("Redefinir", GUILayout.Height(26)))
            {
                Plugin.LayoutElementos[Plugin.ElementoSelecionado] = (1.0f, Vector2.zero);
                inst.Config.Bind("UI_" + Plugin.ElementoSelecionado, "Escala", 1.0f).Value = 1.0f;
                inst.Config.Save();
            }
            GUILayout.EndHorizontal();
        }

        private void DesenharChat()
        {
            var labelStyle = EstiloLabel(14, FontStyle.Bold, Color.white);
            GUILayout.Label("Notas / Comandos rápidos", labelStyle);
            Plugin.TextoNotas = GUILayout.TextArea(Plugin.TextoNotas ?? "", GUILayout.Height(180));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Salvar", GUILayout.Height(28)))
                Plugin.SalvarNotasNoDisco();
            if (GUILayout.Button("Limpar", GUILayout.Height(28)))
                Plugin.LimparNotas();
            GUILayout.EndHorizontal();
        }

        private void DesenharPartida()
        {
            var inst = Plugin.Instance;
            if (inst == null) return;

            var labelStyle = EstiloLabel(14, FontStyle.Bold, Color.white);
            GUILayout.Label("Contador de Tarefas", labelStyle);
            string[] modos = { "Em tempo real", "Apenas por reunião" };
            inst.ModoContadorCfg.Value = GUILayout.SelectionGrid(
                inst.ModoContadorCfg.Value,
                modos,
                2,
                GUI.skin.button,
                GUILayout.Height(32)
            );
        }
    }
}
