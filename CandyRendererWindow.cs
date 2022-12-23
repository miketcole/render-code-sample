namespace Candy.Renderer.UI {
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;

    using Unity.EditorCoroutines.Editor;
    
    using UnityEditor;
    using UnityEditor.SceneManagement;
    
    using UnityEngine;
    using UnityEngine.Networking;
    using UnityEngine.SceneManagement;
    using UnityEngine.UIElements;

    using UnityObject = UnityEngine.Object;
    
    /// <summary>
    /// Main window class of the Candy Renderer.
    /// </summary>
    public class CandyRendererWindow : EditorWindow {
        /// <summary>
        /// Represents a list of renderer step UI items.
        /// </summary>
        private class RendererStepItemList : VisualListItem<RendererStepItem> { }

        /// <summary>
        /// The window's title.
        /// </summary>
        private const string WINDOW_TITLE = "Candy Renderer";

        /// <summary>
        /// The location in the Unity Editor menu for the Renderer Window.
        /// </summary>
        private const string MENU_ROOT = "Candy Playback Engine/Renderer/";

        /// <summary>
        /// The priority of the Renderer Window entries in the menu.
        /// </summary>
        private const int MENU_ROOT_INDEX = 1000;

        private const string RENDER_CONFIGS_URL_FORMAT = "https://realtimecandy.com/Build?f=get_render_configs&asset_path={0}";
        private const string COLLECTIBLE_DATA_URL_FORMAT = "https://realtimecandy.com/Build?f=get_collectible_details&asset_path={0}";

        private enum State {
            Idle,
            FetchingRenderConfigurations,
            WaitingForPlayModeToStartRendering,
            WaitingForScenesData,
            WaitingForRendererToStart,
            Error,
            Rendering
        }

        private bool IsRendering => m_state == State.Rendering || m_state == State.WaitingForPlayModeToStartRendering || m_state == State.WaitingForRendererToStart;
        
        private bool HaveActiveRendererSteps {
            get {
                if (m_selectedRenderConfigurationIndex < 0 ||
                    m_selectedRenderConfigurationIndex >= m_configurations.Length) {
                    return false;
                }
                
                return m_configurations[m_selectedRenderConfigurationIndex].Steps.Any(step=>step.isEnabled);
            }
        }

        private static readonly GUIContent ConfigurationLabel = new GUIContent("Configuration", "The configuration to render");
        private static readonly GUIContent ExitPlayModeLabel = new GUIContent("Exit Play Mode", "To exit the Play mode when the renderer stops.");
        private static readonly GUIContent VerboseModeLabel = new GUIContent("Verbose Mode", "To log in Verbose mode while rendering.");
        private static readonly GUIContent CleanupLabel = new GUIContent("Cleanup", "To delete local renders once completed.");
        private static readonly GUIContent CollectibleVersionLabel = new GUIContent("Version", "To delete local renders once completed.");
        private static readonly GUIContent DuplicateLabel = new GUIContent("Duplicate");
        private static readonly GUIContent DeleteLabel = new GUIContent("Delete");
        private static readonly GUIContent WaitingForPlayModeLabel = new GUIContent("Waiting for Play Mode to start...");
        private static readonly GUIContent WaitingForScenesDataLabel = new GUIContent("Waiting for all scenes to load to start...");
        private static readonly GUIContent WaitingForRendererLabel = new GUIContent("Waiting for renderer to start...");
        
        [SerializeField] private CandyRendererConfiguration[] m_configurations;
        private string[] m_configurationNames;
        private State m_state = State.Idle;
        private int m_selectedRenderConfigurationIndex = 0;
        private bool m_exitPlayMode = true;
        private int m_version = 1;
        private bool m_verboseMode = true;
        private bool m_cleanup = false;
        private Button m_renderButtonIcon;
        private Button m_renderButton;
        private VisualElement m_renderConfigurationOptionsPanel;
        private VisualElement m_collectibleVersionPanel;
        private VisualElement m_exitPlayModePanel;
        private VisualElement m_verboseModePanel;
        private VisualElement m_cleanupPanel;
        private VisualElement m_optionsPanel;
        private VisualElement m_rendererStepsPanel;
        private PanelSplitter m_panelSplitter;
        private VisualElement m_addNewRenderStepPanel;
        private RendererStepItemList m_rendererStepsListItem;
        private VisualElement m_parametersControl;
        private VisualElement m_renderOptionPanel;
        private RendererStepItem m_selectedRendererStepItem;
        private string m_currentErrorMessage;
        
        // This is for getting access to the inspector for CandyRendererStep
        [SerializeField] private CandyRendererStep selectedRendererStep;
        
        [MenuItem(MENU_ROOT + "Renderer Window", false, MENU_ROOT_INDEX)]
        private static void ShowRendererWindow() {
            GetWindow(typeof(CandyRendererWindow), false, WINDOW_TITLE);
        }

        private void OnEnable() {
            this.StartCoroutine(FetchConfigurations());
            CreateView();
            RegisterCallbacks();
        }

        private void OnDestroy() {
            if (IsRendering) {
                StopRendering();
            }

            UnregisterCallbacks();
        }
        
        private void ClearView() {
            var root = rootVisualElement;
            root.Clear();
        }

        private void RegisterCallbacks() {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += UpdateInternal;
            EditorSceneManager.sceneOpened += OnSceneOpened;
        }
        
        private void UnregisterCallbacks() {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= UpdateInternal;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
        }
        
        private void OnSceneOpened(Scene scene, OpenSceneMode mode) {
            if (mode == OpenSceneMode.Single) {
                ClearView();
                this.StartCoroutine(FetchConfigurations());
                CreateView();
            }
        }

        private void CreateView() {
            minSize = new Vector2(560.0f, 200.0f);
            const string pathPrefix1 = "Assets/CandyRenderer/Editor/Assets/Styles/renderer.uss";
            string lightOrDark = EditorGUIUtility.isProSkin ? "renderer_darkSkin" : "renderer_lightSkin";
            string pathPrefix2 = $"Assets/CandyRenderer/Editor/Assets/Styles/{lightOrDark}.uss";
            VisualElement root = rootVisualElement;
            StyleSheet sheet1 = AssetDatabase.LoadAssetAtPath<StyleSheet>(pathPrefix1);
            StyleSheet sheet2 = AssetDatabase.LoadAssetAtPath<StyleSheet>(pathPrefix2);
            bool sheetNotFound = sheet1 == null || sheet2 == null;
            if (sheetNotFound) {
                return;
            }

            root.styleSheets.Add(sheet1);
            root.styleSheets.Add(sheet2);

            root.style.flexDirection = FlexDirection.Column;
            root.focusable = true;

            // TOP BAR
            VisualElement mainControls = new VisualElement {
                style = {
                    flexDirection = FlexDirection.Row,
                    minHeight = 100.0f
                }
            };
            root.Add(mainControls);

            VisualElement controlLeftPane = new VisualElement {
                style = {
                    minWidth = 180.0f,
                    maxWidth = 450.0f,
                    flexDirection = FlexDirection.Row,
                }
            };

            controlLeftPane.style.flexGrow = 0.5f;

            VisualElement controlRightPane = new VisualElement {
                style = {
                    flexDirection = FlexDirection.Column,
                }
            };

            controlRightPane.style.flexGrow = 0.5f;
            mainControls.Add(controlLeftPane);
            mainControls.Add(controlRightPane);
            controlLeftPane.AddToClassList("StandardPanel");
            controlRightPane.AddToClassList("StandardPanel");

            m_renderButtonIcon = new Button(OnRenderButtonClick) {
                name = "renderingIcon",
                style = {
                    backgroundImage = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/CandyRenderer/Editor/Assets/record_button.png"),
                },
                tooltip = "Start the rendering\n\nThis automatically activates the Play mode first (if not activated yet)."
            };
            controlLeftPane.Add(m_renderButtonIcon);

            VisualElement leftButtonsStack = new VisualElement {
                style = {
                    flexDirection = FlexDirection.Column,
                }
            };

            leftButtonsStack.style.flexGrow = 1.0f;

            m_renderButton = new Button(OnRenderButtonClick) {
                name = "renderButton",
                tooltip = "Start/Stop rendering\n\nStarting rendering automatically activates the Play mode first (if not activated yet)."
            };

            UpdateRenderButtonText();
            leftButtonsStack.Add(m_renderButton);

            m_renderConfigurationOptionsPanel = new IMGUIContainer(() => {
                PrepareGUIState(m_renderConfigurationOptionsPanel.layout.width);
                if (m_configurations == null) {
                    return;
                }
                
                int value = EditorGUILayout.Popup(ConfigurationLabel,
                    m_selectedRenderConfigurationIndex, m_configurationNames);
                if (value != m_selectedRenderConfigurationIndex) {
                    m_selectedRenderConfigurationIndex = value;
                    ReloadRendererSteps();
                }
                else {
                    m_selectedRenderConfigurationIndex = value;
                }
            });

            m_renderConfigurationOptionsPanel.style.flexGrow = 1.0f;
            leftButtonsStack.Add(m_renderConfigurationOptionsPanel);
            controlLeftPane.Add(leftButtonsStack);

            VisualElement rightButtonsStack = new VisualElement {
                style = {
                    flexDirection = FlexDirection.Column,
                }
            };

            m_exitPlayModePanel = new IMGUIContainer(() => {
                PrepareGUIState(m_exitPlayModePanel.layout.width);
                m_exitPlayMode = EditorGUILayout.Toggle(ExitPlayModeLabel, m_exitPlayMode);
            }) {
                name = "exitPlayMode"
            };

            m_exitPlayModePanel.style.flexGrow = 1.0f;
            rightButtonsStack.Add(m_exitPlayModePanel);

            m_verboseModePanel = new IMGUIContainer(() => {
                PrepareGUIState(m_verboseModePanel.layout.width);
                m_verboseMode = EditorGUILayout.Toggle(VerboseModeLabel, m_verboseMode);
            }) {
                name = "verboseMode"
            };

            m_verboseModePanel.style.flexGrow = 1.0f;
            rightButtonsStack.Add(m_verboseModePanel);
            
            m_cleanupPanel = new IMGUIContainer(() => {
                PrepareGUIState(m_cleanupPanel.layout.width);
                m_cleanup = EditorGUILayout.Toggle(CleanupLabel, m_cleanup);
            }) {
                name = "cleanup"
            };

            m_cleanupPanel.style.flexGrow = 1.0f;
            rightButtonsStack.Add(m_cleanupPanel);
            
            m_collectibleVersionPanel = new IMGUIContainer(() => {
                PrepareGUIState(m_collectibleVersionPanel.layout.width);
                m_version = EditorGUILayout.IntField(CollectibleVersionLabel, m_version);
            }) {
                name = "collectibleVersion"
            };
            m_collectibleVersionPanel.style.flexGrow = 1.0f;
            rightButtonsStack.Add(m_collectibleVersionPanel);
            controlRightPane.Add(rightButtonsStack);

            m_optionsPanel = new ScrollView();
            m_optionsPanel.name = "optionsPanelScrollView";

            m_optionsPanel.style.flexGrow = 1.0f;
            m_optionsPanel.style.left = m_optionsPanel.style.right = 0;

            VisualElement stepsAndParameters = new VisualElement {
                style = {
                    alignSelf = Align.Stretch,
                    flexDirection = FlexDirection.Row,
                }
            };

            stepsAndParameters.style.flexGrow = 1.0f;

            m_rendererStepsPanel = new VisualElement {
                name = "rendererStepsPanel",
                style = {
                    width = 200.0f,
                    minWidth = 150.0f,
                    maxWidth = 500.0f
                }
            };

            m_rendererStepsPanel.AddToClassList("StandardPanel");
            m_panelSplitter = new PanelSplitter(m_rendererStepsPanel);

            stepsAndParameters.Add(m_rendererStepsPanel);
            stepsAndParameters.Add(m_panelSplitter);
            stepsAndParameters.Add(m_optionsPanel);
            m_optionsPanel.AddToClassList("StandardPanel");
            root.Add(stepsAndParameters);

            Label addRendererStepButton = new Label("+ Add Renderer Step");
            addRendererStepButton.style.flexGrow = 1.0f;

            addRendererStepButton.AddToClassList("RendererStepsListHeader");

            m_addNewRenderStepPanel = new VisualElement {
                name = "addRenderStepsButton",
                style = {flexDirection = FlexDirection.Row},
                tooltip = "Add a new renderer step to the configuration"
            };

            m_addNewRenderStepPanel.Add(addRendererStepButton);
            m_addNewRenderStepPanel.RegisterCallback<MouseUpEvent>(evt => ShowNewRendererStepMenu());

            m_rendererStepsListItem = new RendererStepItemList() {
                name = "rendererStepList"
            };

            m_rendererStepsListItem.style.flexGrow = 1.0f;
            m_rendererStepsListItem.focusable = true;

            m_rendererStepsListItem.OnItemContextMenu += OnRendererStepContextMenu;
            m_rendererStepsListItem.OnSelectionChanged += OnRendererStepSelectionChanged;
            m_rendererStepsListItem.OnItemRename += item => item.StartRenaming();
            m_rendererStepsListItem.OnContextMenu += ShowNewRendererStepMenu;

            m_rendererStepsPanel.Add(m_addNewRenderStepPanel);
            m_rendererStepsPanel.Add(m_rendererStepsListItem);

            m_parametersControl = new VisualElement {
                style = {
                    minWidth = 300.0f,
                }
            };

            m_parametersControl.style.flexGrow = 1.0f;

            m_renderOptionPanel = new IMGUIContainer(OnRenderOptionsGUI) {
                name = "renderOptions"
            };

            m_renderOptionPanel.style.flexGrow = 1.0f;

            VisualElement statusBar = new VisualElement {
                name = "statusBar"
            };

            statusBar.Add(new IMGUIContainer(UpdateRenderingProgressGUI));
            root.Add(statusBar);
            m_parametersControl.Add(m_renderOptionPanel);
            m_optionsPanel.Add(m_parametersControl);
            //ReloadRendererSteps();
        }

        private void OnRendererStepContextMenu(RendererStepItem rendererStepItem) {
            GenericMenu contextMenu = new GenericMenu();

            if (IsRendering) {
                contextMenu.AddDisabledItem(DuplicateLabel);
                contextMenu.AddDisabledItem(DeleteLabel);
            }
            else {
                contextMenu.AddItem(DuplicateLabel, false,
                    data => {
                        DuplicateRendererStep((RendererStepItem)data);
                    }, rendererStepItem);

                contextMenu.AddItem(DeleteLabel, false,
                    data => {
                        DeleteRendererStep((RendererStepItem)data);
                    }, rendererStepItem);
            }

            contextMenu.ShowAsContext();
        }

        private void OnRendererStepSelectionChanged() {
            m_selectedRendererStepItem = m_rendererStepsListItem.Selection;

            foreach (RendererStepItem r in m_rendererStepsListItem.Items) {
                r.SetItemSelected(m_selectedRendererStepItem == r);
            }


            Repaint();
        }

        private static void ShowMessageInStatusBar(string msg, MessageType messageType) {
            Rect r = EditorGUILayout.GetControlRect();

            if (messageType != MessageType.None) {
                Rect iconR = r;
                iconR.width = iconR.height;

                Texture2D icon = messageType == MessageType.Error
                    ? StatusBarHelper.ErrorIcon
                    : (messageType == MessageType.Warning ? StatusBarHelper.WarningIcon : StatusBarHelper.InfoIcon);

                GUI.DrawTexture(iconR, icon);

                r.xMin = iconR.xMax + 5.0f;
            }

            GUIStyle style = messageType == MessageType.Error
                ? StatusBarHelper.ErrorStyle
                : (messageType == MessageType.Warning ? StatusBarHelper.WarningStyle : StatusBarHelper.InfoStyle);
            style.clipping = TextClipping.Overflow;
            GUI.Label(r, msg, style);
        }

        private void UpdateRenderingProgressGUI() {
            if (m_state == State.Idle) {
                ShowMessageInStatusBar("Not currently rendering", MessageType.None);
                return;
            }
            
            if (m_state == State.FetchingRenderConfigurations) {
                ShowMessageInStatusBar("Fetching render configurations from Real Time Candy", MessageType.Info);
                return;
            }
            
            if (m_state == State.Error) {
                ShowMessageInStatusBar(m_currentErrorMessage, MessageType.Error);
                return;
            }

            if (m_state == State.WaitingForPlayModeToStartRendering) {
                EditorGUILayout.LabelField(WaitingForPlayModeLabel);
                return;
            }
            
            if (m_state == State.WaitingForScenesData) {
                EditorGUILayout.LabelField(WaitingForScenesDataLabel);
                return;
            }
            
            if (m_state == State.WaitingForRendererToStart) {
                EditorGUILayout.LabelField(WaitingForRendererLabel);
                return;
            }

            if (CandyRenderer.RenderPlaybackActive) {
                var settings = CandyRenderer.Settings;
                string label;
                switch (settings.RecordingMethod) {
                    case RecordingMethod.Genlock:
                        ShowMessageInStatusBar("Rendering in progress, see log for details.", MessageType.Info);
                        label = $"{CandyRenderer.CurrentFrameIndex} frame(s) processed";
                        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(), 0, label);
                        break;
                    case RecordingMethod.UnityRecorder:
                        ShowMessageInStatusBar("Rendering in progress, see log for details.", MessageType.Info);
                        break;
                    case RecordingMethod.TimeScale:
                        if (CandyRenderer.ActivePass != null) {
                            if (CandyRenderer.ActivePass.PassType == CandyRenderer.PlaybackPhase.AudioPass) {
                                ShowMessageInStatusBar("Audio pass in progress, see log for details.", MessageType.Info);
                            } else if (CandyRenderer.ActivePass.PassType == CandyRenderer.PlaybackPhase.VideoPass) {
                                ShowMessageInStatusBar("Frames pass in progress, see log for details.", MessageType.Info);
                            }
                        } else {
                            ShowMessageInStatusBar("Waiting for pass to start, see log for details.", MessageType.Info);
                        }
                        
                        label = $"{CandyRenderer.CurrentFrameIndex}/{CandyRenderer.TotalsFramesProcessed} frame(s) processed";
                        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(), 0, label);
                        break;
                }
            }
        }

        private void OnRenderButtonClick() {
            if (m_state == State.Error) {
                m_state = State.Idle;
            }

            switch (m_state) {
                case State.Idle:
                    StartRendering();
                    break;
                case State.WaitingForPlayModeToStartRendering:
                case State.Rendering:
                    StopRendering();
                    break;
                default:
                    break;
            }

            UpdateRenderButtonText();
        }

        private void UpdateRenderButtonText() {
            m_renderButton.text = m_state == State.Rendering ? "STOP RENDERING" : "START RENDERING";
        }

        public void StartRendering() {
            if (EditorUtility.scriptCompilationFailed) {
                return;
            }

            if (EditorApplication.isPlaying) {
                EditorApplication.isPaused = false;
                // Already in play mode, so start rendering now
                StartRenderingInternal();
            }
            else if (m_state == State.Idle) {
                // Not playing yet and idle
                m_state = State.WaitingForPlayModeToStartRendering;
                EditorApplication.isPlaying = true;
            }
        }

        private void StartRenderingInternal() {
            if (m_verboseMode) {
                Debug.Log("Start Rendering.");
            }

            List<CandyRendererStep> rendererSteps = new List<CandyRendererStep>();
            foreach (RendererStepItem rendererStepItem in m_rendererStepsListItem.Items) { 

                if (rendererStepItem.IsEnabled) {
                    rendererSteps.Add(rendererStepItem.RendererStep);
                }
            }

            CandyRenderer.Start(rendererSteps.ToArray(), m_version, m_verboseMode, m_cleanup, null, m_exitPlayMode ? () => {EditorApplication.isPlaying = false;} : null);
            m_state = State.WaitingForRendererToStart;
        }
        
        public void StopRendering() {
            if (IsRendering) {
                StopRenderingInternal();
            }
        }

        private void StopRenderingInternal() {
            if (m_verboseMode) {
                Debug.Log("Stop Rendering.");
            }

            // Stop the renderer if it is currently running
            if (CandyRenderer.RenderPlaybackActive) {
                CandyRenderer.Stop();
            }

            m_state = State.Idle;
        }

        private void UpdateInternal() {
            if (!EditorApplication.isPlaying) {
                if (m_state == State.Rendering) {
                    StopRenderingInternal();
                }
            }
            else if (m_state == State.WaitingForScenesData && AreAllSceneDataLoaded()) {
                StartRenderingInternal();
            }
            else if (m_state == State.WaitingForRendererToStart && CandyRenderer.RenderPlaybackActive) {
                m_state = State.Rendering;
            }

            bool enable = !IsRendering;
            m_addNewRenderStepPanel.SetEnabled(enable);
            m_renderConfigurationOptionsPanel.SetEnabled(enable);
            m_exitPlayModePanel.SetEnabled(enable);
            m_verboseModePanel.SetEnabled(enable);
            m_optionsPanel.SetEnabled(enable);
            m_rendererStepsListItem.Items.ForEach(item => item.SetEnabled(enable));

            if (m_state != State.Error && m_state != State.FetchingRenderConfigurations &&  HaveActiveRendererSteps) {
                if (IsRendering) {
                    SetRenderButtonsEnabled(EditorApplication.isPlaying);
                }
                else {
                    SetRenderButtonsEnabled(!EditorUtility.scriptCompilationFailed);
                }
            }
            else {
                SetRenderButtonsEnabled(false);
            }

            UpdateRenderButtonText();

            if (m_state == State.Rendering) {
                if (!CandyRenderer.RenderPlaybackActive) {
                    StopRenderingInternal();
                }

                Repaint();
            }
        }
        
        private void SetRenderButtonsEnabled(bool enabled) {
            m_renderButton.SetEnabled(enabled);
            m_renderButtonIcon.SetEnabled(enabled);
        }

        [RuntimeInitializeOnLoadMethod]
        private static void RuntimeInit() {
            CandyRendererWindow[] windows = Resources.FindObjectsOfTypeAll<CandyRendererWindow>();

            if (windows != null && windows.Length > 0) {
                CandyRendererWindow win = windows[0];
                if (win.m_state == State.WaitingForPlayModeToStartRendering) {
                    win.RequestStartRendering();
                }
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange obj) {
            if (obj == PlayModeStateChange.ExitingPlayMode) {
                if (m_state == State.Rendering) {
                    StopRenderingInternal();
                }
                else {
                    m_state = State.Idle;
                }
            }
        }

        private static bool AreAllSceneDataLoaded() {
            for (int i = 0; i < SceneManager.sceneCount; ++i) {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) {
                    return false;
                }
            }

            return true;
        }

        private void RequestStartRendering() {
            if (m_verboseMode) {
                Debug.Log("Prepare and wait all scenes to load.");
            }
            
            m_state = State.WaitingForScenesData;
        }
        
        private IEnumerator FetchConfigurations() {
            if (m_state != State.Idle) {
                if (m_state == State.WaitingForScenesData) {
                    ReloadRendererSteps();
                }
                yield break;
            }
            
            m_state = State.FetchingRenderConfigurations;

            CandyCollectible collectible = FindObjectOfType<CandyCollectible>();

            if (collectible == null) {
                m_currentErrorMessage = "Current scene does not have a collectible";
                m_state = State.Error;
                yield break;
            }

            if (string.IsNullOrEmpty(collectible.AssetPath)) {
                m_currentErrorMessage = "Collectible is missing asset path";
                m_state = State.Error;
                yield break;
            }
            
            string url = string.Format(RENDER_CONFIGS_URL_FORMAT, collectible.AssetPath);
            
            using (UnityWebRequest www = UnityWebRequest.Get(url)) {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success) {
                    string response = www.downloadHandler.text;
                    if (string.IsNullOrEmpty(response)) {
                        m_state = State.Error;
                        yield break;
                    }

                    CandyRendererConfigurations configurations = JsonUtility.FromJson<CandyRendererConfigurations>(response);

                    if (configurations == null || configurations.Configurations == null ||
                        configurations.Configurations.Length == 0) {
                        m_currentErrorMessage = "Render configurations from Real Time Candy are invalid";
                        m_state = State.Error;
                        yield break;
                    }

                    m_configurations = configurations.Configurations;
                    
                    foreach (CandyRendererConfiguration configuration in m_configurations) {
                        foreach (CandyRendererStep step in configuration.Steps) {
                            step.isEnabled = true;
                        }
                    }

                    // Cache all the configuration names
                    m_configurationNames = m_configurations != null
                        ? m_configurations.Select(x => x.Name).ToArray()
                        : Array.Empty<string>();
                    
                    ReloadRendererSteps();
                } else {
                    m_currentErrorMessage = "Could not fetch render configurations from Real Time Candy";
                    m_state = State.Error;
                    yield break;
                }
            }
            
            url = string.Format(COLLECTIBLE_DATA_URL_FORMAT, collectible.AssetPath);
            using (UnityWebRequest www = UnityWebRequest.Get(url)) {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success) {
                    string response = www.downloadHandler.text;
                    if (string.IsNullOrEmpty(response)) {
                        m_currentErrorMessage = "Could not fetch collectible data from Real Time Candy";
                        m_state = State.Error;
                        yield break;
                    }
                    
                    CollectibleDataResponse dataResponse = JsonUtility.FromJson<CollectibleDataResponse>(response);

                    if (dataResponse.data == null || !string.IsNullOrEmpty(dataResponse.error)) {
                        m_currentErrorMessage = "Collectible data from Real Time Candy is invalid";
                        m_state = State.Error;
                        yield break;
                    }
                    
                    m_version = dataResponse.data.collectible_version;
                    if (m_state != State.Error) {
                        m_state = State.Idle;
                    }
                }
            }
        }

        private void OnRenderOptionsGUI() {
            PrepareGUIState(m_renderOptionPanel.layout.width);

            if (m_selectedRendererStepItem != null) {
                selectedRendererStep = m_selectedRendererStepItem.RendererStep;

                if (selectedRendererStep == null) {
                    EditorGUILayout.LabelField("Error while displaying the recorder inspector");
                }
                else {
                    SerializedObject serializedObject = new SerializedObject(this);
                    SerializedProperty serializedProperty = serializedObject.FindProperty("selectedRendererStep");
                    EditorGUILayout.PropertyField(serializedProperty);
                    serializedObject.ApplyModifiedProperties();
                }
            }
            else {
                EditorGUILayout.LabelField("No renderer step selected");
            }
        }

        private void ReloadRendererSteps() {
            if (m_selectedRenderConfigurationIndex < 0 ||
                m_selectedRenderConfigurationIndex >= m_configurations.Length) {
                Debug.LogError("Selected configuration index is out of bounds.");
                return;
            }

            RendererStepItem[] rendererStepItems = m_configurations[m_selectedRenderConfigurationIndex]
                .Steps.Select(CreateRendererStepItem).ToArray();

            foreach (RendererStepItem rendererStepItem in rendererStepItems) {
                //rendererStepItem.UpdateState();
            }

            m_rendererStepsListItem.Reload(rendererStepItems);
        }
        
        private void ShowNewRendererStepMenu() {
            CandyCollectible collectible = CandyRenderer.Collectible;
            if (collectible == null) {
                return;
            }

            Array options = collectible.GetPlaybackModes();
            GenericMenu newRendererStepMenu = new GenericMenu();

            foreach (var option in options) {
                AddRendererStepToMenu(option.ToString(), newRendererStepMenu);
            }

            newRendererStepMenu.ShowAsContext();
        }
        
        void AddRendererStepToMenu(string name, GenericMenu menu) {
            if (IsRendering) {
                menu.AddDisabledItem(new GUIContent(name));
            } else {
                menu.AddItem(new GUIContent(name), false, data => OnAddNewRendererStep(name), null);
            }
        }

        RendererStepItem CreateRendererStepItem(CandyRendererStep rendererStep) {
            var rendererStepItem = new RendererStepItem(rendererStep);
            rendererStepItem.OnEnableStateChanged += enabled => {
                if (enabled) {
                    m_rendererStepsListItem.Selection = rendererStepItem;
                }
            };

            return rendererStepItem;
        }
        
        private void AddLastAndSelect(CandyRendererStep rendererStep, string desiredName, bool enabled) {
            m_configurations[m_selectedRenderConfigurationIndex].AddStep(rendererStep);
            
            RendererStepItem item = CreateRendererStepItem(rendererStep);
            m_rendererStepsListItem.Add(item);
            m_rendererStepsListItem.Selection = item;
            m_rendererStepsListItem.Focus();
        }
        
        private void DuplicateRendererStep(RendererStepItem item) {
            CandyRendererStep candidate = item.RendererStep;
            CandyRendererStep copy = (CandyRendererStep)candidate.Clone();
            AddLastAndSelect(copy, item.name, item.IsEnabled);
        }

        private void DeleteRendererStep(RendererStepItem item) {
           m_configurations[m_selectedRenderConfigurationIndex].RemoveStep(item.RendererStep);
           m_rendererStepsListItem.Remove(item);
        }
        
        void OnAddNewRendererStep(string playbackMode) {
            CandyRendererStep rendererStep = new CandyRendererStep();
            rendererStep.name = playbackMode;
            rendererStep.playbackMode = playbackMode;
            rendererStep.isEnabled = true;
            AddLastAndSelect(rendererStep, playbackMode, true);
            m_state = State.Idle;
        }

        private static void PrepareGUIState(float contextWidth) {
            EditorGUIUtility.labelWidth = Mathf.Min(Mathf.Max(contextWidth * 0.45f - 40, 100), 160);
        }
    }
}