using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

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
            window.minSize = new Vector2(420, 280);
            window.Show();
        }

        void CreateGUI()
        {
            var settings = UnityInjectionSettings.Instance;
            settings.hideFlags &= ~HideFlags.NotEditable;
            rootUIAsset.CloneTree(rootVisualElement);
            // var tglEnabled = rootVisualElement.Q<Toggle>("tglEnabled");
            var lstSource = rootVisualElement.Q<ListView>("lstSource");
            // var lstError = rootVisualElement.Q<ListView>("lstError");
            var sltEditor = rootVisualElement.Q<TypeField>("sltEditor");
            var sltRuntime = rootVisualElement.Q<TypeField>("sltRuntime");

            var injectionRecords = InjectionDriver.GetInjectionInfos()
                .GroupBy(i => i.InjectedMethod.Module.Assembly)
                .Select(g =>
                {
                    var assembly = Path.GetFullPath(g.Key.GetAssemblyPath());
                    var errorRecord = UnityInjectionSettings.Instance.errorRecords.FirstOrDefault(r => r.assemblyPath == assembly);
                    var message = errorRecord.message;
                    return new
                    {
                        title = assembly + " +" + g.Count(),
                        hasError = !string.IsNullOrEmpty(message),
                        message,
                    };
                })
                .OrderBy(c => !c.hasError)
                .ToArray()
                ;

            lstSource.makeItem = () =>
            {
                var uiElement = new Foldout();
                var text = new Label()
                {
                    name = "txtMessage"
                };
                uiElement.Add(text);
                return uiElement;
            };

            lstSource.bindItem = (v, i) =>
            {
                var fold = v as Foldout;
                var txt = v.Q<Label>("txtMessage");
                var data = injectionRecords[i];
                fold.text = data.title;
                txt.text = data.message;
                fold.style.color = (data.hasError ? Color.red : Color.white) * 0.95f;
                fold.value &= data.hasError;
            };

            lstSource.itemsSource = injectionRecords;

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
                GetImplementInstance<IEditorInjectionImplement>(0).OnDomainReload();
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

        private T GetImplementInstance<T>(int index) where T : class, IInjectionImplement
        {
            var type = typeof(T);
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(type.IsAssignableFrom)
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .OrderBy(t => t.AssemblyQualifiedName)
                .ToList()
                ;
            return Activator.CreateInstance(types[index]) as T;
        }
    }
}
