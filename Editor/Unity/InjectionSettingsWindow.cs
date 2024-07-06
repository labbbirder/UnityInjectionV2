using UnityEditor;
using UnityEngine;
using System;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using System.IO;
using System.Linq;

namespace BBBirder.UnityInjection.Editor
{

    public class InjectionSettingsWindow : EditorWindow
    {
        [SerializeField] VisualTreeAsset rootUIAsset;
        [SerializeField] VisualTreeAsset elementUIAsset;

        [MenuItem("Tools/bbbirder/Unity Injection")]
        public static void ShowWindow()
        {
            var window = GetWindow<InjectionSettingsWindow>();
            window.titleContent = new GUIContent("Unity Injection");
            window.Show();
        }

        void CreateGUI()
        {
            var settings = UnityInjectionSettings.instance;
            settings.hideFlags &= ~HideFlags.NotEditable;
            rootUIAsset.CloneTree(rootVisualElement);
            // var tglEnabled = rootVisualElement.Q<Toggle>("tglEnabled");
            var lstSource = rootVisualElement.Q<ListView>("lstSource");
            var lstError = rootVisualElement.Q<ListView>("lstError");
            var sltEditor = rootVisualElement.Q<TypeField>("sltEditor");
            var sltRuntime = rootVisualElement.Q<TypeField>("sltRuntime");
            lstSource.makeItem = elementUIAsset.CloneTree;
            // lstSource.bindItem = (v, i) =>
            // {
            //     var data = settings.injectionSources[i];
            //     v.Q<Label>().text = Path.GetFileName(data.path);
            // };

            sltRuntime.SetEnabled(false);

            sltEditor.index = Math.Max(0, sltEditor.choices.IndexOf(Type.GetType(settings.EditorImplement)));
            sltEditor.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue != evt.previousValue)
                {
                    settings.EditorImplement = evt.newValue.AssemblyQualifiedName;
                    InjectionDriver.Instance.EditorImplement = Activator.CreateInstance(evt.newValue) as IEditorInjectionImplement;
                }
            });

            sltRuntime.index = Math.Max(0, sltRuntime.choices.IndexOf(Type.GetType(settings.RuntimeImplement)));
            sltRuntime.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue != evt.previousValue)
                {
                    settings.RuntimeImplement = evt.newValue.AssemblyQualifiedName;
                    InjectionDriver.Instance.RuntimeImplement = Activator.CreateInstance(evt.newValue) as IRuntimeInjectionImplement;
                }
            });

            var btnInject = rootVisualElement.Q<Button>("btnInject");
            btnInject.clicked += () =>
            {
                InjectionDriver.Instance.InstallAllAssemblies();
            };

            var serializedObject = new SerializedObject(settings);
            rootVisualElement.Bind(serializedObject);
            rootVisualElement.TrackSerializedObjectValue(serializedObject, so =>
            {
                Logger.loggerLevel = settings.loggerLevel;
                settings.Save();
            });
        }
    }
}
