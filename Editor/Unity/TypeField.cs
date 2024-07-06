using System;
using System.Linq;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace BBBirder.UnityInjection.Editor
{

    public class TypeField : PopupField<Type>
    {
        public enum TypeDisplay
        {
            Name,
            FullName,
            AssemblyQualifiedName,
        }
        private new class UxmlFactory : UxmlFactory<TypeField, UxmlTraits> { }
        private new class UxmlTraits : PopupField<Type>.UxmlTraits
        {
            public UxmlBoolAttributeDescription m_AllowNull = new() { name = "allow-null" };
            public UxmlEnumAttributeDescription<TypeDisplay> m_TypeDisplay = new() { name = "type-display" };
            public UxmlBoolAttributeDescription m_IncludeAbstract = new() { name = "include-abstract" };
            public UxmlTypeAttributeDescription<object> m_Type = new() { name = "type" };
            public UxmlIntAttributeDescription m_Index = new() { name = "index" };
            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                ; ((TypeField)ve).AllowNull = m_AllowNull.GetValueFromBag(bag, cc);
                ; ((TypeField)ve).typeDisplay = m_TypeDisplay.GetValueFromBag(bag, cc);
                ; ((TypeField)ve).IncludeAbstract = m_IncludeAbstract.GetValueFromBag(bag, cc);
                ; ((TypeField)ve).Type = m_Type.GetValueFromBag(bag, cc);
                ; ((TypeField)ve).index = m_Index.GetValueFromBag(bag, cc);
            }
        }
        private bool m_AllowNull;
        public bool AllowNull
        {
            get => m_AllowNull;
            set
            {
                if (m_AllowNull != value)
                {
                    m_AllowNull = value;
                    if (value && (choices.Count == 0 || choices[0] != null))
                    {
                        choices.Insert(0, null);
                    }
                    if (!value && choices.Count > 0 && choices[0] == null)
                    {
                        choices.RemoveAt(0);
                    }
                }
            }
        }
        private bool m_IncludeAbstract;
        public bool IncludeAbstract
        {
            get => m_IncludeAbstract;
            set
            {
                if (m_IncludeAbstract != value)
                {
                    m_IncludeAbstract = value;
                    UpdateChoices(Type, IncludeAbstract);
                }
            }
        }
        private Type m_Type;
        public Type Type
        {
            get => m_Type;
            set
            {
                if (m_Type != value)
                {
                    m_Type = value;
                    UpdateChoices(Type, IncludeAbstract);
                }
            }
        }
        public TypeDisplay typeDisplay { get; set; }

        public TypeField()
        {
            formatListItemCallback =
            formatSelectedValueCallback = GetTypeName;
        }

        public string GetTypeName(Type type)
        {
            return type is null ? "<null>" : typeDisplay switch
            {
                TypeDisplay.AssemblyQualifiedName => type.AssemblyQualifiedName,
                TypeDisplay.FullName => type.FullName,
                _ => type.Name,
            };
        }

        public void UpdateChoices(Type type, bool includeAbstract)
        {
            if (type is null)
            {
                choices = null;
                return;
            }
            var validTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(type.IsAssignableFrom)
                .Where(t => includeAbstract || !t.IsAbstract)
                .OrderBy(t => t.AssemblyQualifiedName)
                .ToList()
                ;
            choices = validTypes;
        }
    }
}
