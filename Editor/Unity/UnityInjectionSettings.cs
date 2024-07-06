
using System;
using UnityEditor;
using UnityEngine;

namespace BBBirder.UnityInjection.Editor
{
    [FilePath(SAVE_PATH, FilePathAttribute.Location.ProjectFolder)]
    public class UnityInjectionSettings : ScriptableSingleton<UnityInjectionSettings>
    {
        const string SAVE_PATH = "ProjectSettings/UnityInjectionSettings.asset";
        [Serializable]
        public struct AssemblyRecord
        {
            public string path;
            public long lastModifyTime;
        }


        [SerializeField]
        public bool enabled = true;

        [SerializeField]
        public bool autoInstallForRuntime = true;

        [SerializeField]
        public LoggerLevel loggerLevel = LoggerLevel.Info;

        [SerializeField]
        public string EditorImplement = typeof(EditorWeaveImplement).AssemblyQualifiedName;

        [SerializeField]
        public string RuntimeImplement = typeof(WeavingFixImplement).AssemblyQualifiedName;

        // public IEditorInjectionImplement CreateEditorImplementInstance()
        //     => GetImplementInstance<IEditorInjectionImplement>(EditorImplement);

        // public IRuntimeInjectionImplement CreateRuntimeImplementInstance()
        //     => GetImplementInstance<IRuntimeInjectionImplement>(RuntimeImplement);

        // private T GetImplementInstance<T>(int index) where T : class, IInjectionImplement
        // {
        //     var type = typeof(T);
        //     var types = AppDomain.CurrentDomain.GetAssemblies()
        //         .SelectMany(a => a.GetTypes())
        //         .Where(type.IsAssignableFrom)
        //         .Where(t => !t.IsAbstract)
        //         .OrderBy(t => t.AssemblyQualifiedName)
        //         .ToList()
        //         ;
        //     return Activator.CreateInstance(types[index]) as T;
        // }

        public void Save() => base.Save(true);
        // public Assembly[] GetAssemblies()
        // {
        //     var hashset = injectionSources
        //         .Select(r => Path.Join(Directory.GetCurrentDirectory(), r.path))
        //         .Select(p => p.Replace('\\', '/'))
        //         .ToHashSet();
        //     return AppDomain.CurrentDomain.GetAssemblies()
        //         .Where(a => hashset.Contains(a.GetAssemblyPath()))
        //         .ToArray();
        // }
    }
}
