using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BBBirder.UnityInjection.Editor
{
    [FilePath(SAVE_PATH, FilePathAttribute.Location.ProjectFolder)]
    public class UnityInjectionSettings : ScriptableObject
    {
        const string SAVE_PATH = "ProjectSettings/UnityInjectionSettings.asset";

        static UnityInjectionSettings _instance;
        public static UnityInjectionSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    try
                    {
                        var content = File.ReadAllText(SAVE_PATH);
                        _instance = CreateInstance<UnityInjectionSettings>();
                        JsonUtility.FromJsonOverwrite(content, _instance);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(e.Message);
                        _instance = null;
                    }
                }

                if (_instance == null)
                {
                    _instance = CreateInstance<UnityInjectionSettings>();
                    _instance.Save();
                }

                return _instance;
            }
        }

        [SerializeField]
        public bool enabled = true;

        [SerializeField]
        public List<TokenRecord> errorRecords = new();

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

        public void Save()
        {
            if (File.Exists(SAVE_PATH)) File.Delete(SAVE_PATH);

            var dirname = Path.GetDirectoryName(SAVE_PATH);
            if (!Directory.Exists(dirname)) Directory.CreateDirectory(dirname);

            File.WriteAllText(SAVE_PATH, JsonUtility.ToJson(this, true));
        }

        [Serializable]
        public struct TokenRecord
        {
            public string assemblyPath;
            public string message;
        }
    }
}
