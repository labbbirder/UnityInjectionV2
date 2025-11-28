using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace BBBirder.UnityInjection
{
    public static class ReflectionHelper
    {

        internal class GenericContext
        {
            public Type[] klassGenericArguments;
            public Type[] methodGenericArguments;
        }

        public static object CreateInstance(Type type)
        {
            if (!type.IsSubclassOf(typeof(UnityEngine.Object)) && HasDefaultConstructor(type))
            {
                return Activator.CreateInstance(type);
            }
            else
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                return RuntimeHelpers.GetUninitializedObject(type);
            }

            static bool HasDefaultConstructor(Type type)
            {
                var ctors = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                return ctors.Any(c => c.GetParameters().Length == 0);
            }
        }

        public static MethodBase GetMethod(Expression<Action> expression)
        {
            if (expression.Body is MethodCallExpression mthExpr)
            {
                return mthExpr.Method;
            }

            return null;
        }

        public static T CastDelegate<T>(this Delegate del) where T : MulticastDelegate
        {
            return (T)CastDelegate(del, typeof(T));
        }

        public static Delegate CastDelegate(this Delegate del, Type delegateType)
        {
            if (delegateType == del.GetType()) return del;
            return Delegate.CreateDelegate(delegateType, del.Method);
        }

        public static MethodBase GetMethod<T>(Expression<Func<T>> expression)
        {
            if (expression.Body is MethodCallExpression mthExpr)
            {
                return mthExpr.Method;
            }

            if (expression.Body is NewExpression newExpr)
            {
                return newExpr.Constructor;
            }

            if (expression.Body is MemberExpression memExpr)
            {
                if (memExpr.Member is PropertyInfo pi)
                {
                    return pi.GetGetMethod(true);
                }

                return null;
            }

            if (expression.Body is BinaryExpression binExpr)
            {
                return binExpr.Method;
            }

            if (expression.Body is UnaryExpression unaExpr)
            {
                return unaExpr.Method;
            }

            return null;
        }

        public static MemberInfo GetMember(Expression<Func<object>> expression)
        {
            if (expression.Body is MemberExpression memExpr)
            {
                return memExpr.Member;
            }
            return null;
        }

        internal static string GetSignature(this Type t, GenericContext genericContext = null)
        {
            var includesGenericContext = genericContext != null;
            if (!includesGenericContext && t.IsGenericType)
            {
                t = t.GetGenericTypeDefinition();
            }
            var builder = new StringBuilder();
            // append namespase
            if (!string.IsNullOrEmpty(t.Namespace))
            {
                builder.Append(t.Namespace);
                builder.Append('.');
            }

            // append nesting types
            var declaringTypes = GetDeclaringTypes(t).Reverse();
            builder.AppendJoin('+', declaringTypes.Select(t => t.Name));
            // append generic arguments
            var genericArgumentTypes = t.GetGenericArguments();
            if (genericArgumentTypes.Length > 0 && includesGenericContext)
            {
                builder.Append('[');
                for (int i = 0; i < genericArgumentTypes.Length; i++)
                {
                    var gat = genericArgumentTypes[i];
                    if (i > 0) builder.Append(",");
                    if (gat.IsGenericTypeParameter)
                    {
                        builder.Append(".T");
                        builder.Append(gat.GenericParameterPosition);
                    }
                    else if (gat.IsGenericMethodParameter)
                    {

                        builder.Append(".T");
                        builder.Append(gat.GenericParameterPosition + genericContext.klassGenericArguments.Length);
                    }
                    else
                    {
                        builder.Append(gat.GetSignature(genericContext));
                    }
                }

                builder.Append(']');
            }
            return builder.ToString();

            IEnumerable<Type> GetDeclaringTypes(Type type)
            {
                while (type != null)
                {
                    yield return type;
                    type = type.DeclaringType;
                }
            }
        }

        internal static string GetSignature(this MethodBase method)
        {
            // implement of CLS Rule 43

            // clear generic parameters
            var moduleHandle = method.DeclaringType.Assembly.GetModules()[0].ModuleHandle;
            var unconstructedDeclaringType = method.DeclaringType;
            if (unconstructedDeclaringType.IsGenericType)
            {
                unconstructedDeclaringType = unconstructedDeclaringType.GetGenericTypeDefinition();
            }
            var methodHandle = moduleHandle.GetRuntimeMethodHandleFromMetadataToken(method.MetadataToken);
            var klassHandle = moduleHandle.GetRuntimeTypeHandleFromMetadataToken(unconstructedDeclaringType.MetadataToken);
            method = MethodBase.GetMethodFromHandle(methodHandle, klassHandle);

            var builder = new StringBuilder();
            // append method name
            builder.Append(method.Name);
            if (method.IsGenericMethod)
            {
                builder.Append('`');
                builder.Append(method.GetGenericArguments().Length);
            }

            // append method parameters
            var klassGenericArguments = method.DeclaringType.GetGenericArguments();
            builder.Append('<');
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameterDefinition = parameters[i];
                var parameterType = parameterDefinition.ParameterType;
                if (i > 0)
                {
                    builder.Append(",");
                }
                if (parameterType.IsGenericTypeParameter)
                {
                    builder.Append("!");
                    builder.Append(parameterType.GenericParameterPosition);
                }
                else if (parameterType.IsGenericMethodParameter)
                {
                    builder.Append("!");
                    builder.Append(parameterType.GenericParameterPosition + klassGenericArguments.Length);
                }
                else
                {
                    var argSig = GetSignature(parameterType, new GenericContext()
                    {
                        klassGenericArguments = klassGenericArguments,
                        methodGenericArguments = method.GetGenericArguments(),
                    });
                    builder.Append(argSig);
                }
            }

            builder.Append('>');
            return builder.ToString();
        }

        public static string GetAssemblyPath(this Assembly assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            if (assembly.IsDynamic)
            {
                return null;
            }

            if (File.Exists(assembly.Location))
            {
                return assembly.Location;
            }

            return null;
        }
    }
}
